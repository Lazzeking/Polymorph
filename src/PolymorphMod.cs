using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;

namespace Polymorph;

public class PolymorphMod : ModSystem, IPolymorphChoices
{
    public const string IconPath = "textures/gui/cycle.svg";
    public const string ConfigFile = "polymorph.json";
    public const string ConflictsFile = "polymorph-conflicts.txt";
    public const string QuernPrefix = "quern:";

    private static Harmony? _harmony;
    private static readonly ConditionalWeakTable<ICoreAPI, PolymorphMod> Instances = new();

    private readonly Dictionary<(string Player, string Arrangement), string> _choices = new();
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);

    private ICoreClientAPI? _clientApi;
    private ICoreServerAPI? _serverApi;
    private Action? _saveGameLoaded;
    private IClientNetworkChannel? _clientChannel;
    private IServerNetworkChannel? _serverChannel;
    private PolymorphChoiceDialog? _choiceDialog;
    private List<GridRecipe> _matches = new();
    private string _arrangement = "";
    private string _activeKey = "";

    public LoadedTexture? Icon { get; private set; }

    public bool HasAlternatives => _matches.Count > 1;

    public bool Locked => false;

    public static PolymorphMod? Of(ICoreAPI api)
    {
        return Instances.TryGetValue(api, out PolymorphMod? mod) ? mod : null;
    }

    public override void Start(ICoreAPI api)
    {
        Instances.AddOrUpdate(api, this);
        if (_harmony != null)
        {
            return;
        }

        _harmony = new Harmony("polymorph");
        _harmony.PatchAllUncategorized(typeof(PolymorphMod).Assembly);
        if (PatchCategory(_harmony, api, QuernChoices.PatchCategory, "querns keep their vanilla results"))
        {
            PatchCategory(_harmony, api, QuernChoices.HandbookPatchCategory, "the handbook shows only the default quern results");
            PatchCategory(_harmony, api, GrindingOverloads.PatchCategory, "a mod that adds a quern result over another one replaces it, as in vanilla");
        }

        PatchCategory(_harmony, api, CookingChoices.PatchCategory, "cooking pots cook the first matching recipe, as in vanilla");
    }

    // If another mod changed what a category patches so one patch fails, the whole category is taken back out
    // and the rest of the mod keeps working.
    private static bool PatchCategory(Harmony harmony, ICoreAPI api, string category, string fallback)
    {
        try
        {
            harmony.PatchCategory(typeof(PolymorphMod).Assembly, category);
            return true;
        }
        catch (Exception exception)
        {
            api.Logger.Warning("Polymorph could not apply its {0} patches, so {1}: {2}", category, fallback, exception.Message);
            foreach (MethodBase original in harmony.GetPatchedMethods().ToList())
            {
                Patches? info = Harmony.GetPatchInfo(original);
                if (info == null)
                {
                    continue;
                }

                foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    if (patch.owner == harmony.Id && InCategory(patch.PatchMethod.DeclaringType, category))
                    {
                        harmony.Unpatch(original, patch.PatchMethod);
                    }
                }
            }

            return false;
        }
    }

    private static bool InCategory(Type? type, string category)
    {
        return type != null && type.GetCustomAttributes<HarmonyPatchCategory>(false).Any(attribute => attribute.info.category == category);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        _clientApi = api;
        _clientChannel = api.Network
            .RegisterChannel("polymorph")
            .RegisterMessageType<SelectRecipePacket>()
            .RegisterMessageType<DenyListPacket>()
            .RegisterMessageType<QuernChoicePacket>()
            .RegisterMessageType<GrindingOverloadsPacket>()
            .RegisterMessageType<CookingChoicePacket>()
            .SetMessageHandler<DenyListPacket>(OnDenyList)
            .SetMessageHandler<GrindingOverloadsPacket>(OnGrindingOverloads);
        _choiceDialog = new PolymorphChoiceDialog(api);
        if (_harmony != null)
        {
            SurvivalInventoryPatch.PatchOverhaulLib(_harmony, api);
        }
        Icon = api.Gui.LoadSvgWithPadding(new AssetLocation(Mod.Info.ModID, IconPath), 64, 64, 4, null);
        if (Icon == null)
        {
            Mod.Logger.Warning("Recipe cycle icon was not found at assets/" + Mod.Info.ModID + "/" + IconPath + ".");
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        _serverApi = api;
        _saveGameLoaded = () => LoadDenyList(api);
        api.Event.SaveGameLoaded += _saveGameLoaded;
        _serverChannel = api.Network
            .RegisterChannel("polymorph")
            .RegisterMessageType<SelectRecipePacket>()
            .RegisterMessageType<DenyListPacket>()
            .RegisterMessageType<QuernChoicePacket>()
            .RegisterMessageType<GrindingOverloadsPacket>()
            .RegisterMessageType<CookingChoicePacket>()
            .SetMessageHandler<SelectRecipePacket>(OnRecipeSelected)
            .SetMessageHandler<QuernChoicePacket>(QuernChoices.OnChoicePacket)
            .SetMessageHandler<CookingChoicePacket>(CookingChoices.OnChoicePacket);
        api.Event.PlayerNowPlaying += SendGrindingOverloads;
        api.Event.PlayerNowPlaying += SendDenyList;
        api.ChatCommands
            .Create("polymorph")
            .WithDescription("Writes crafting conflicts and reloads the denylist.")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("conflicts")
                .WithDescription("Write a file of crafting grids that can make more than one item.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ => WriteConflicts(api))
            .EndSubCommand()
            .BeginSubCommand("reload")
                .WithDescription("Read the denylist again, report unknown recipes, and send it to connected players.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ => ReloadDenyList(api))
            .EndSubCommand()
            .BeginSubCommand("syntax")
                .WithDescription("Check the denylist file without changing it.")
                .RequiresPrivilege(Privilege.kick)
                .HandleWith(_ => CheckDenySyntax(api))
            .EndSubCommand();
    }

    public override void Dispose()
    {
        if (_serverApi != null && _saveGameLoaded != null)
        {
            _serverApi.Event.SaveGameLoaded -= _saveGameLoaded;
        }

        if (_choiceDialog?.IsOpened() == true)
        {
            _choiceDialog.TryClose();
        }

        Icon?.Dispose();
        Icon = null;
        QuernChoices.Reset();
        _harmony?.UnpatchAll("polymorph");
        _harmony = null;
    }

    public void CloseChoices()
    {
        if (_choiceDialog?.IsOpened() == true)
        {
            _choiceDialog.TryClose();
        }
    }

    public void CloseChoicesFor(object owner)
    {
        if (ChoicesOpenFor(owner))
        {
            _choiceDialog!.TryClose();
        }
    }

    public bool ChoicesOpenFor(object owner)
    {
        return _choiceDialog?.IsOpened() == true && _choiceDialog.Owner == owner;
    }

    public void RefreshChoicesFor(object owner, IReadOnlyList<ItemStack?> outputs, int active, Action<int> onSelected)
    {
        if (ChoicesOpenFor(owner))
        {
            _choiceDialog!.Refresh(outputs, active, onSelected);
        }
    }

    // Opens the result panel beside a choice button, or closes it when it is already open.
    public void ToggleChoiceDialog(double renderX, double renderY, double outerWidth, double outerHeight, IReadOnlyList<ItemStack?> outputs, int active, Action<int> onSelected, object owner, bool below = false)
    {
        if (_choiceDialog == null)
        {
            return;
        }

        if (_choiceDialog.IsOpened())
        {
            _choiceDialog.TryClose();
            return;
        }

        if (outputs.Count < 2)
        {
            return;
        }

        _choiceDialog.Open(renderX, renderY, outerWidth, outerHeight, outputs, active, onSelected, owner, below);
    }

    public void SendQuernChoice(BlockPos pos, string key)
    {
        _clientChannel?.SendPacket(new QuernChoicePacket { X = pos.X, Y = pos.Y, Z = pos.Z, Dimension = pos.dimension, Key = key });
    }

    public void SendCookingChoice(BlockPos pos, string key)
    {
        _clientChannel?.SendPacket(new CookingChoicePacket { X = pos.X, Y = pos.Y, Z = pos.Z, Dimension = pos.dimension, Key = key });
    }

    public static bool TryResolve(InventoryCraftingGrid grid)
    {
        PolymorphMod? mod = grid.Api.ModLoader.GetModSystem<PolymorphMod>();
        if (mod == null)
        {
            return false;
        }

        ItemSlot[] slots = CraftingGridAccess.Slots(grid);
        int gridSize = CraftingGridAccess.GridSize(grid);
        IPlayer? player = ((InventoryBasePlayer)grid).Player;
        if (player == null)
        {
            string? uid = CraftingGridAccess.PlayerUid(grid);
            if (!string.IsNullOrEmpty(uid))
            {
                player = grid.Api.World.PlayerByUid(uid);
            }
        }

        grid.MatchingRecipe = null;
        CraftingGridAccess.OutputSlot(grid).Itemstack = null;

        List<GridRecipe> matches = FindMatches(mod, grid, slots, gridSize, player);
        if (matches.Count == 0)
        {
            grid.DirtySlots.Add(CraftingGridAccess.GridSizeSquared(grid));
            mod.SetCurrent(player, slots, matches, "");
            return true;
        }

        string arrangement = ArrangementKey(slots);
        string? saved = player == null ? null : mod.ChoiceFor(player.PlayerUID, arrangement);
        GridRecipe chosen = matches[0];
        if (saved != null)
        {
            GridRecipe? remembered = matches.Find(recipe => RecipeKey(recipe) == saved);
            if (remembered != null)
            {
                chosen = remembered;
            }
        }

        CraftingGridAccess.FoundMatch(grid, chosen);
        mod.SetCurrent(player, slots, matches, RecipeKey(chosen));
        return true;
    }

    public void ToggleChoices(double renderX, double renderY, double outerWidth, double outerHeight)
    {
        List<GridRecipe> matches = _matches;
        int active = matches.FindIndex(recipe => RecipeKey(recipe) == _activeKey);
        ItemStack?[] outputs = matches.Select(recipe => recipe.Output?.ResolvedItemStack).ToArray();
        ToggleChoiceDialog(renderX, renderY, outerWidth, outerHeight, outputs, active, index => SelectChoice(RecipeKey(matches[index])), this);
    }

    public void SelectChoice(string recipeKey)
    {
        IPlayer? player = _clientApi?.World.Player;
        if (player == null || _matches.Count < 2 || _arrangement.Length == 0 || recipeKey == _activeKey)
        {
            return;
        }

        _choices[(player.PlayerUID, _arrangement)] = recipeKey;
        _clientChannel?.SendPacket(new SelectRecipePacket { Arrangement = _arrangement, RecipeKey = recipeKey });
        Rerun(player);
    }

    private void OnRecipeSelected(IServerPlayer fromPlayer, SelectRecipePacket packet)
    {
        if (string.IsNullOrEmpty(packet.Arrangement) || string.IsNullOrEmpty(packet.RecipeKey))
        {
            return;
        }

        _choices[(fromPlayer.PlayerUID, packet.Arrangement)] = packet.RecipeKey;
        Rerun(fromPlayer);
    }

    private void SetCurrent(IPlayer? player, ItemSlot[] slots, List<GridRecipe> matches, string activeKey)
    {
        if (player != null && activeKey.Length > 0)
        {
            _choices[(player.PlayerUID, ArrangementKey(slots))] = activeKey;
        }

        if (gridIsClient(player))
        {
            string arrangement = ArrangementKey(slots);
            bool changed = _arrangement != arrangement || _activeKey != activeKey || !SameMatches(_matches, matches);
            _arrangement = arrangement;
            _matches = matches;
            _activeKey = activeKey;
            if (changed)
            {
                CloseChoicesFor(this);
            }
        }
    }

    private bool gridIsClient(IPlayer? player)
    {
        return _clientApi != null && player is IClientPlayer;
    }

    private string? ChoiceFor(string playerUid, string arrangement)
    {
        return _choices.TryGetValue((playerUid, arrangement), out string? key) ? key : null;
    }

    private static bool SameMatches(List<GridRecipe> current, List<GridRecipe> next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        for (int i = 0; i < current.Count; i++)
        {
            if (RecipeKey(current[i]) != RecipeKey(next[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void Rerun(IPlayer player)
    {
        string? inventoryName = player.InventoryManager.GetInventoryName("craftinggrid");
        if (inventoryName == null)
        {
            return;
        }

        if (player.InventoryManager.GetInventory(inventoryName) is not InventoryCraftingGrid grid)
        {
            return;
        }

        CraftingGridAccess.FindMatchingRecipe(grid);
    }

    private static List<GridRecipe> FindMatches(PolymorphMod mod, InventoryCraftingGrid grid, ItemSlot[] slots, int gridSize, IPlayer? player)
    {
        var candidates = new List<GridRecipe>();
        var seen = new HashSet<GridRecipe>();
        foreach (ItemSlot slot in slots)
        {
            ItemStack? stack = slot.Itemstack;
            if (stack == null || stack.StackSize <= 0)
            {
                continue;
            }

            foreach (KeyValuePair<IRecipeIngredientBase, List<IRecipeBase>> pair in grid.Api.World.FastSearchRecipesByIngredient)
            {
                if (!pair.Key.SatisfiesAsIngredient(stack, false))
                {
                    continue;
                }

                foreach (IRecipeBase recipeBase in pair.Value)
                {
                    if (recipeBase is GridRecipe recipe && recipe.Enabled && seen.Add(recipe))
                    {
                        candidates.Add(recipe);
                    }
                }
            }
        }

        var matches = new List<GridRecipe>();
        AppendMatches(matches, candidates, mod, player, grid, slots, gridSize, shapeless: false);
        AppendMatches(matches, candidates, mod, player, grid, slots, gridSize, shapeless: true);
        return matches;
    }

    private static void AppendMatches(List<GridRecipe> matches, List<GridRecipe> candidates, PolymorphMod mod, IPlayer? player, InventoryCraftingGrid grid, ItemSlot[] slots, int gridSize, bool shapeless)
    {
        foreach (GridRecipe recipe in candidates)
        {
            if (recipe.Shapeless != shapeless || mod.IsDenied(recipe))
            {
                continue;
            }

            if (recipe.Matches(player!, grid.Api.World, slots, gridSize))
            {
                matches.Add(recipe);
            }
        }
    }

    private List<string> LoadDenyList(ICoreServerAPI api)
    {
        PolymorphConfig? config;
        try
        {
            config = api.LoadModConfig<PolymorphConfig>(ConfigFile);
        }
        catch (Exception exception)
        {
            api.Logger.Error("Could not read the Polymorph denylist: {0}", exception.Message);
            config = new PolymorphConfig();
        }

        if (config == null)
        {
            config = new PolymorphConfig();
            api.StoreModConfig(config, ConfigFile);
        }

        (List<string> accepted, List<string> invalid) = ScanDenyEntries(api, config.Deny);
        foreach (string entry in invalid)
        {
            api.Logger.Warning("Polymorph denylist entry does not match a grid recipe, quern result or cooking recipe: {0}", entry);
        }

        ReplaceDenied(accepted.ToArray());
        return invalid;
    }

    private static (List<string> Accepted, List<string> Invalid) ScanDenyEntries(ICoreServerAPI api, string[]? names)
    {
        var accepted = new List<string>();
        var invalid = new List<string>();
        if (names == null)
        {
            return (accepted, invalid);
        }

        HashSet<string>? quernKeys = null;

        foreach (string name in names)
        {
            string trimmed = name.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (IsKnownDenyEntry(api, trimmed, ref quernKeys))
            {
                accepted.Add(trimmed);
            }
            else
            {
                invalid.Add(trimmed);
            }
        }

        return (accepted, invalid);
    }

    private static bool IsKnownDenyEntry(ICoreServerAPI api, string entry, ref HashSet<string>? quernKeys)
    {
        if (entry.StartsWith(QuernPrefix, StringComparison.OrdinalIgnoreCase))
        {
            quernKeys ??= AllQuernDenyKeys(api);
            return quernKeys.Contains(entry);
        }

        if (entry.StartsWith(CookingConflicts.DenyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return CookingConflicts.IsKnown(api, entry);
        }

        foreach (GridRecipe recipe in api.World.GridRecipes)
        {
            if (EntryDenies(entry, recipe))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EntryDenies(string entry, GridRecipe recipe)
    {
        foreach (string key in DenyKeys(recipe))
        {
            if (entry.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private TextCommandResult ReloadDenyList(ICoreServerAPI api)
    {
        List<string> invalid = LoadDenyList(api);
        int players = 0;
        foreach (IPlayer player in api.World.AllOnlinePlayers)
        {
            if (player is not IServerPlayer serverPlayer)
            {
                continue;
            }

            SendDenyList(serverPlayer);
            Rerun(serverPlayer);
            players++;
        }

        string message = "Reloaded " + _denied.Count + " denied results for " + players + " connected players.";
        if (invalid.Count == 0)
        {
            return TextCommandResult.Success(message);
        }

        return TextCommandResult.Success(message + " No matching grid recipe, quern result or cooking recipe for: " + string.Join(", ", invalid));
    }

    private TextCommandResult CheckDenySyntax(ICoreServerAPI api)
    {
        PolymorphConfig? config;
        try
        {
            config = api.LoadModConfig<PolymorphConfig>(ConfigFile);
        }
        catch (Exception exception)
        {
            return TextCommandResult.Success("The denylist file could not be read: " + exception.Message);
        }

        if (config == null)
        {
            return TextCommandResult.Success("No denylist file yet. Nothing is denied.");
        }

        (List<string> accepted, List<string> invalid) = ScanDenyEntries(api, config.Deny);
        if (invalid.Count == 0)
        {
            return TextCommandResult.Success("Denylist is ok. " + accepted.Count + " entries match a grid recipe, quern result or cooking recipe. The list was not changed.");
        }

        return TextCommandResult.Success(
            "Denylist was not changed. " + invalid.Count + " entries do not match a grid recipe, quern result or cooking recipe: " + string.Join(", ", invalid));
    }

    private void SendDenyList(IServerPlayer player)
    {
        _serverChannel?.SendPacket(new DenyListPacket { Names = _denied.ToArray() }, player);
    }

    // Item types come from the server, so the quern results it kept from mod patches come along with them.
    private void SendGrindingOverloads(IServerPlayer player)
    {
        if (_serverApi != null)
        {
            _serverChannel?.SendPacket(new GrindingOverloadsPacket { Entries = GrindingOverloads.For(_serverApi).ToArray() }, player);
        }
    }

    private void OnGrindingOverloads(GrindingOverloadsPacket packet)
    {
        if (_clientApi == null)
        {
            return;
        }

        GrindingOverloads.Replace(_clientApi, packet.Entries);
        QuernChoices.Reset();
        GrindingHandbook.ReloadOpenPages(_clientApi);
    }

    private void OnDenyList(DenyListPacket packet)
    {
        ReplaceDenied(packet.Names);
        IPlayer? player = _clientApi?.World.Player;
        if (player != null)
        {
            Rerun(player);
        }

        if (_choiceDialog?.Owner is BlockEntityQuern quern)
        {
            QuernChoiceSource.Refresh(quern);
        }

        if (_clientApi != null)
        {
            GrindingHandbook.ReloadOpenPages(_clientApi);
            CookingChoices.RedrawOpenDialogs(_clientApi);
        }
    }

    private void ReplaceDenied(string[]? names)
    {
        _denied.Clear();
        if (names == null)
        {
            return;
        }

        foreach (string name in names)
        {
            string trimmed = name.Trim();
            if (trimmed.Length > 0)
            {
                _denied.Add(trimmed);
            }
        }
    }

    private bool IsDenied(GridRecipe recipe)
    {
        if (_denied.Count == 0)
        {
            return false;
        }

        foreach (string key in DenyKeys(recipe))
        {
            if (_denied.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    // cooking:<recipe code>
    public bool IsDenied(CookingRecipe recipe)
    {
        return _denied.Count > 0 && _denied.Contains(CookingConflicts.DenyId(recipe));
    }

    public bool IsDenied(CollectibleObject input, GrindingProperties option)
    {
        if (_denied.Count == 0)
        {
            return false;
        }

        foreach (string key in QuernDenyKeys(input, option))
        {
            if (_denied.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    // quern:<input>@<output> and quern:<input>@<output> x<size>
    private static IEnumerable<string> QuernDenyKeys(CollectibleObject input, GrindingProperties option)
    {
        ItemStack? output = QuernChoices.Output(option);
        if (input.Code == null || output?.Collectible?.Code == null)
        {
            yield break;
        }

        string prefix = QuernPrefix + input.Code + "@";
        yield return prefix + output.Collectible.Code;
        yield return prefix + QuernChoices.Key(option);
    }

    private static string QuernDenyId(CollectibleObject input, GrindingProperties option)
    {
        return QuernPrefix + input.Code + "@" + QuernChoices.Key(option);
    }

    private static HashSet<string> AllQuernDenyKeys(ICoreServerAPI api)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((CollectibleObject input, List<GrindingProperties> options) in Grindables(api))
        {
            foreach (GrindingProperties option in options)
            {
                keys.UnionWith(QuernDenyKeys(input, option));
            }
        }

        return keys;
    }

    // Every item a quern can grind, with all its results, denied or not.
    private static IEnumerable<(CollectibleObject Input, List<GrindingProperties> Options)> Grindables(ICoreServerAPI api)
    {
        foreach (CollectibleObject? collectible in api.World.Collectibles)
        {
            if (collectible?.Code == null)
            {
                continue;
            }

            List<GrindingProperties> options;
            try
            {
                options = QuernChoices.AllOptions(api.World, QuernChoices.StackOf(collectible));
            }
            catch (Exception)
            {
                continue;
            }

            if (options.Count > 0)
            {
                yield return (collectible, options);
            }
        }
    }

    private static IEnumerable<string> DenyKeys(GridRecipe recipe)
    {
        AssetLocation? name = recipe.Name;
        if (name == null)
        {
            yield break;
        }

        string full = name.ToString();
        string path = name.Path;
        string output = OutputLabel(recipe);
        string? code = OutputCode(recipe);
        yield return full;
        yield return path;
        yield return full + "@" + output;
        yield return path + "@" + output;
        if (code == null)
        {
            yield break;
        }

        yield return full + "@" + code;
        yield return path + "@" + code;
    }

    private TextCommandResult WriteConflicts(ICoreServerAPI api)
    {
        try
        {
            string folder = api.GetOrCreateDataPath("ModConfig");
            string path = Path.Combine(folder, ConflictsFile);
            List<ConflictGroup> groups = ConflictGroups(api);
            List<(CollectibleObject Input, List<GrindingProperties> Options)> querns = Grindables(api)
                .Where(grindable => grindable.Options.Count > 1)
                .OrderBy(grindable => grindable.Input.Code.ToString(), StringComparer.Ordinal)
                .ToList();
            List<(string Pot, List<CookingRecipe> Recipes)> pots = CookingConflicts.Groups(api);
            File.WriteAllText(path, ConflictReport(groups) + QuernReport(querns) + Environment.NewLine + CookingConflicts.Report(pots, IsDenied));
            return TextCommandResult.Success("Wrote " + groups.Count + " grid, " + querns.Count + " quern and " + pots.Count + " cooking conflict groups to " + path);
        }
        catch (Exception exception)
        {
            return TextCommandResult.Success("Could not write recipe conflicts: " + exception.Message);
        }
    }

    internal static string ArrangementKey(ItemSlot[] slots)
    {
        var parts = new string[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            ItemStack? stack = slots[i].Itemstack;
            parts[i] = stack == null || stack.StackSize <= 0
                ? "-"
                : stack.Collectible.Code.ToString();
        }

        return string.Join("|", parts);
    }

    internal static string RecipeKey(GridRecipe recipe)
    {
        ItemStack? stack = recipe.Output?.ResolvedItemStack;
        string output = stack?.Collectible?.Code?.ToString() ?? "none";
        int quantity = stack?.StackSize ?? 0;
        string name = recipe.Name?.ToString() ?? "";
        return name + "#" + output + "#" + quantity + "#" + (recipe.Shapeless ? "s" : "g");
    }

    private static List<ConflictGroup> ConflictGroups(ICoreServerAPI api)
    {
        var scans = new List<RecipeScan>();
        var buckets = new Dictionary<(int Count, int Quantity), List<RecipeScan>>();
        foreach (GridRecipe recipe in api.World.GridRecipes)
        {
            if (!recipe.Enabled || !TryScan(recipe, out RecipeScan? scan) || scan == null)
            {
                continue;
            }

            scans.Add(scan);
            if (!buckets.TryGetValue((scan.Count, scan.Quantity), out List<RecipeScan>? bucket))
            {
                bucket = new List<RecipeScan>();
                buckets[(scan.Count, scan.Quantity)] = bucket;
            }

            bucket.Add(scan);
        }

        var inventory = new InventoryGeneric(9, "polymorph-conflicts", api);
        var slots = new ItemSlot[9];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = inventory[i];
        }

        var groups = new List<ConflictGroup>();
        var seen = new HashSet<string>();
        foreach (RecipeScan probe in scans)
        {
            if (!probe.CanProbe || !TryFillGrid(probe, slots))
            {
                ClearSlots(slots);
                continue;
            }

            var gridCodes = new HashSet<string>(StringComparer.Ordinal);
            int filled = 0;
            int quantity = 0;
            foreach (ItemSlot slot in slots)
            {
                ItemStack? stack = slot.Itemstack;
                string? code = stack?.Collectible?.Code?.ToString();
                if (code == null || stack == null || stack.StackSize <= 0)
                {
                    continue;
                }

                filled++;
                quantity += stack.StackSize;
                gridCodes.Add(code);
            }

            if (filled != probe.Count || quantity != probe.Quantity || !buckets.TryGetValue((filled, quantity), out List<RecipeScan>? bucket))
            {
                ClearSlots(slots);
                continue;
            }

            var hits = new List<GridRecipe>();
            var outputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (RecipeScan candidate in bucket)
            {
                if (!CouldMatch(candidate, gridCodes))
                {
                    continue;
                }

                try
                {
                    if (!RecipesMatch(candidate, slots, api.World))
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                string? output = OutputCode(candidate.Recipe);
                if (output != null && outputs.Add(output))
                {
                    hits.Add(candidate.Recipe);
                }
            }

            string grid = GridLabel(slots);
            string layout = GridLayout(slots);
            ClearSlots(slots);
            if (hits.Count < 2)
            {
                continue;
            }

            hits.Sort((left, right) => string.Compare(RecipeKey(left), RecipeKey(right), StringComparison.Ordinal));
            if (seen.Add(string.Join("|", hits.Select(RecipeKey))))
            {
                groups.Add(new ConflictGroup { Grid = grid, Layout = layout, Recipes = hits });
            }
        }

        groups.Sort((left, right) => string.Compare(left.Grid, right.Grid, StringComparison.Ordinal));
        return groups;
    }

    private static bool TryScan(GridRecipe recipe, out RecipeScan? scan)
    {
        scan = null;
        var resolved = recipe.ResolvedIngredients;
        if (resolved == null || resolved.Length == 0)
        {
            return false;
        }

        var ingredients = new IRecipeIngredient[resolved.Length];
        var codes = new List<string>();
        int count = 0;
        int quantity = 0;
        bool canProbe = recipe.Width > 0;
        for (int i = 0; i < resolved.Length; i++)
        {
            CraftingRecipeIngredient? ingredient = resolved[i];
            ingredients[i] = ingredient!;
            if (ingredient == null)
            {
                continue;
            }

            count++;
            quantity += ingredient.Quantity > 0 ? ingredient.Quantity : 1;
            ItemStack? stack = ingredient.ResolvedItemStack;
            string? code = stack?.Collectible?.Code?.ToString();
            if (code == null)
            {
                canProbe = false;
            }
            else
            {
                codes.Add(code);
            }
        }

        if (count == 0)
        {
            return false;
        }

        scan = new RecipeScan
        {
            Recipe = recipe,
            Ingredients = ingredients,
            Codes = codes,
            Count = count,
            Quantity = quantity,
            CanProbe = canProbe
        };
        return true;
    }

    private static bool TryFillGrid(RecipeScan probe, ItemSlot[] slots)
    {
        ClearSlots(slots);
        var resolved = probe.Recipe.ResolvedIngredients;
        if (resolved == null)
        {
            return false;
        }

        int placed = 0;
        for (int i = 0; i < resolved.Length; i++)
        {
            CraftingRecipeIngredient? ingredient = resolved[i];
            ItemStack? stack = ingredient?.ResolvedItemStack;
            if (ingredient == null || stack == null)
            {
                continue;
            }

            int slot = (i / probe.Recipe.Width) * 3 + (i % probe.Recipe.Width);
            if (slot < 0 || slot >= slots.Length || slots[slot].Itemstack != null)
            {
                return false;
            }

            ItemStack clone = stack.Clone();
            clone.StackSize = ingredient.Quantity > 0 ? ingredient.Quantity : 1;
            slots[slot].Itemstack = clone;
            placed++;
        }

        return placed == probe.Count;
    }

    private static void ClearSlots(ItemSlot[] slots)
    {
        foreach (ItemSlot slot in slots)
        {
            slot.Itemstack = null;
        }
    }

    private static bool CouldMatch(RecipeScan recipe, HashSet<string> gridCodes)
    {
        foreach (string code in recipe.Codes)
        {
            if (!gridCodes.Contains(code))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RecipesMatch(RecipeScan recipe, ItemSlot[] slots, IWorldAccessor world)
    {
        if (recipe.Recipe.Shapeless)
        {
            return (bool)ShapeLessMethod.Invoke(recipe.Recipe, new object[] { slots, world, recipe.Ingredients })!;
        }

        if (recipe.Recipe.Width > 3 || recipe.Recipe.Height > 3 || recipe.Recipe.Width <= 0 || recipe.Recipe.Height <= 0)
        {
            return false;
        }

        for (int column = 0; column <= 3 - recipe.Recipe.Width; column++)
        {
            for (int row = 0; row <= 3 - recipe.Recipe.Height; row++)
            {
                if ((bool)AtPositionMethod.Invoke(recipe.Recipe, new object[] { column, row, slots, 3, recipe.Recipe.Width, recipe.Ingredients })!)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? OutputCode(GridRecipe recipe)
    {
        return recipe.Output?.ResolvedItemStack?.Collectible?.Code?.ToString()
            ?? recipe.Output?.Code?.ToString();
    }

    private static string GridLabel(ItemSlot[] slots)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ItemSlot slot in slots)
        {
            ItemStack? stack = slot.Itemstack;
            string? code = stack?.Collectible?.Code?.ToString();
            if (code == null || stack == null || stack.StackSize <= 0)
            {
                continue;
            }

            counts[code] = counts.GetValueOrDefault(code) + stack.StackSize;
        }

        return string.Join(" + ", counts.OrderBy(pair => pair.Key).Select(pair => pair.Key + " x" + pair.Value));
    }

    private static string GridLayout(ItemSlot[] slots)
    {
        var lines = new string[3];
        for (int row = 0; row < 3; row++)
        {
            var cells = new string[3];
            for (int column = 0; column < 3; column++)
            {
                ItemStack? stack = slots[row * 3 + column].Itemstack;
                string? code = stack?.Collectible?.Code?.ToString();
                cells[column] = code == null || stack == null || stack.StackSize <= 0
                    ? "[ ]"
                    : "[" + code + " x" + stack.StackSize + "]";
            }

            lines[row] = "  " + string.Join(" ", cells);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string ConflictReport(List<ConflictGroup> groups)
    {
        var report = new StringBuilder();
        report.AppendLine("Each entry is one filled crafting grid that can make more than one item.");
        report.AppendLine("Copy a deny line into ModConfig/" + ConfigFile + ", inside Deny. Do not copy the entry number.");
        report.AppendLine("The quoted text stops that one result. The same items then make the other result.");
        report.AppendLine("A denied result never crafts, even when it is the only match.");
        report.AppendLine("Run /polymorph syntax to check the file without changing it. Run /polymorph reload to apply the valid lines.");
        report.AppendLine();
        report.AppendLine("Example:");
        report.AppendLine("{");
        report.AppendLine("  \"Deny\": [");
        report.AppendLine("    \"doublerecipetest:recipes/grid/double.json@game:ingot-gold x1\",");
        report.AppendLine("    \"quern:game:flint@game:ingot-gold x1\",");
        report.AppendLine("    \"cooking:stick-gold\"");
        report.AppendLine("  ]");
        report.AppendLine("}");
        report.AppendLine();
        if (groups.Count == 0)
        {
            report.AppendLine("No conflicting grid recipes.");
            return report.ToString();
        }

        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            ConflictGroup group = groups[groupIndex];
            report.Append(groupIndex + 1).AppendLine(".");
            report.AppendLine(group.Layout);
            report.AppendLine("  This grid can make:");
            foreach (GridRecipe recipe in group.Recipes)
            {
                report.Append("  ").Append(OutputLabel(recipe));
                if (IsDenied(recipe))
                {
                    report.Append(" (already denied)");
                }

                report.AppendLine();
                report.Append("  deny: \"").Append(DenyId(recipe)).AppendLine("\"");
            }

            report.AppendLine();
        }

        return report.ToString();
    }

    private string QuernReport(List<(CollectibleObject Input, List<GrindingProperties> Options)> querns)
    {
        var report = new StringBuilder();
        report.AppendLine("Quern");
        report.AppendLine();
        report.AppendLine("Each entry is one item that a quern can grind into more than one result.");
        report.AppendLine("The deny lines go in the same Deny list. A denied result is never ground. When every result of an item is denied, the quern does not grind it.");
        report.AppendLine();
        if (querns.Count == 0)
        {
            report.AppendLine("No item grinds into more than one result.");
            return report.ToString();
        }

        for (int index = 0; index < querns.Count; index++)
        {
            (CollectibleObject input, List<GrindingProperties> options) = querns[index];
            report.Append(index + 1).Append(". ").AppendLine(input.Code.ToString());
            report.AppendLine("  This item can grind into:");
            foreach (GrindingProperties option in options)
            {
                report.Append("  ").Append(QuernChoices.Key(option));
                if (IsDenied(input, option))
                {
                    report.Append(" (already denied)");
                }

                report.AppendLine();
                report.Append("  deny: \"").Append(QuernDenyId(input, option)).AppendLine("\"");
            }

            report.AppendLine();
        }

        return report.ToString();
    }

    private static string DenyId(GridRecipe recipe)
    {
        return RecipeLabel(recipe) + "@" + OutputLabel(recipe);
    }

    private static readonly MethodInfo ShapeLessMethod = AccessTools.Method(typeof(RecipeBase), "MatchesShapeLess")
        ?? throw new MissingMethodException(nameof(RecipeBase), "MatchesShapeLess");

    private static readonly MethodInfo AtPositionMethod = typeof(RecipeBase)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .First(method => method.Name == "MatchesAtPosition" && method.GetParameters().Length == 6);

    private sealed class RecipeScan
    {
        public GridRecipe Recipe = null!;
        public IRecipeIngredient[] Ingredients = Array.Empty<IRecipeIngredient>();
        public List<string> Codes = new();
        public int Count;
        public int Quantity;
        public bool CanProbe;
    }

    private sealed class ConflictGroup
    {
        public string Grid = "";
        public string Layout = "";
        public List<GridRecipe> Recipes = new();
    }

    private static string RecipeLabel(GridRecipe recipe)
    {
        return recipe.Name?.ToString() ?? "(no name)";
    }

    private static string OutputLabel(GridRecipe recipe)
    {
        ItemStack? stack = recipe.Output?.ResolvedItemStack;
        if (stack?.Collectible?.Code != null)
        {
            return stack.Collectible.Code + " x" + stack.StackSize;
        }

        return recipe.Output?.Code?.ToString() ?? "(no output)";
    }
}

[ProtoContract]
public class SelectRecipePacket
{
    [ProtoMember(1)]
    public string Arrangement { get; set; } = "";

    [ProtoMember(2)]
    public string RecipeKey { get; set; } = "";
}

[ProtoContract]
public class QuernChoicePacket
{
    [ProtoMember(1)]
    public int X { get; set; }

    [ProtoMember(2)]
    public int Y { get; set; }

    [ProtoMember(3)]
    public int Z { get; set; }

    [ProtoMember(4)]
    public int Dimension { get; set; }

    [ProtoMember(5)]
    public string Key { get; set; } = "";
}

[ProtoContract]
public class DenyListPacket
{
    [ProtoMember(1)]
    public string[] Names { get; set; } = Array.Empty<string>();
}

public class PolymorphConfig
{
    public string[] Deny = Array.Empty<string>();
}

internal static class CraftingGridAccess
{
    private static readonly FieldInfo SlotsField = AccessTools.Field(typeof(InventoryCraftingGrid), "slots");
    private static readonly FieldInfo GridSizeField = AccessTools.Field(typeof(InventoryCraftingGrid), "GridSize");
    private static readonly FieldInfo GridSizeSquaredField = AccessTools.Field(typeof(InventoryCraftingGrid), "GridSizeSq");
    private static readonly FieldInfo OutputSlotField = AccessTools.Field(typeof(InventoryCraftingGrid), "outputSlot");
    private static readonly FieldInfo PlayerUidField = AccessTools.Field(typeof(InventoryBasePlayer), "playerUID");
    private static readonly MethodInfo FoundMatchMethod = AccessTools.Method(typeof(InventoryCraftingGrid), "FoundMatch");
    private static readonly MethodInfo FindMatchingRecipeMethod = AccessTools.Method(typeof(InventoryCraftingGrid), "FindMatchingRecipe");

    public static ItemSlot[] Slots(InventoryCraftingGrid grid)
    {
        return (ItemSlot[])SlotsField.GetValue(grid)!;
    }

    public static int GridSize(InventoryCraftingGrid grid)
    {
        return (int)GridSizeField.GetValue(grid)!;
    }

    public static int GridSizeSquared(InventoryCraftingGrid grid)
    {
        return (int)GridSizeSquaredField.GetValue(grid)!;
    }

    public static ItemSlot OutputSlot(InventoryCraftingGrid grid)
    {
        return (ItemSlot)OutputSlotField.GetValue(grid)!;
    }

    public static string? PlayerUid(InventoryCraftingGrid grid)
    {
        return (string?)PlayerUidField.GetValue(grid);
    }

    public static void FoundMatch(InventoryCraftingGrid grid, GridRecipe recipe)
    {
        FoundMatchMethod.Invoke(grid, new object[] { recipe });
    }

    public static void FindMatchingRecipe(InventoryCraftingGrid grid)
    {
        FindMatchingRecipeMethod.Invoke(grid, Array.Empty<object>());
    }
}
