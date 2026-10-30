using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Polymorph;

// A barrel uses the first barrel recipe that matches its contents: a sealed one waits for Seal, an instant one crafts
// at once. When several match, each barrel remembers which one to use for that set of recipes, until it is destroyed,
// so automation keeps working once a player has chosen. Until then the barrel waits, and the player who filled it is
// asked to choose.
public static class BarrelChoices
{
    public const string TreeKey = "polymorphBarrelChoices";

    // The barrel patches are applied apart from the others, so a failure here leaves the rest working.
    public const string PatchCategory = "polymorph-barrel";

    private static readonly ConditionalWeakTable<BlockEntityBarrel, BarrelState> States = new();

    private static readonly AccessTools.FieldRef<BlockEntityBarrel, bool> IgnoreChange = AccessTools.FieldRefAccess<BlockEntityBarrel, bool>("ignoreChange");
    private static readonly AccessTools.FieldRef<BlockEntityBarrel, GuiDialogBarrel?> InvDialog = AccessTools.FieldRefAccess<BlockEntityBarrel, GuiDialogBarrel?>("invDialog");
    private static readonly AccessTools.FieldRef<BlockEntityBarrel, MeshData?> CurrentMesh = AccessTools.FieldRefAccess<BlockEntityBarrel, MeshData?>("currentMesh");
    private static readonly MethodInfo FindMatchingRecipeMethod = AccessTools.Method(typeof(BlockEntityBarrel), "FindMatchingRecipe", new[] { typeof(IPlayer) });

    // The player whose action the barrel is reacting to, while they use the block or its window.
    [ThreadStatic] private static IPlayer? _actor;

    // Set while the server applies a choice a player just made, so the barrel crafts instead of asking again.
    [ThreadStatic] private static bool _confirmed;

    // Every recipe that matches the barrel's contents, in the game's order, without denied ones unless asked for.
    public static List<(BarrelRecipe Recipe, int OutSize)> Matches(BlockEntityBarrel barrel, IPlayer? player, bool withDenied = false)
    {
        var matches = new List<(BarrelRecipe, int)>();
        List<BarrelRecipe>? recipes = barrel.Api?.GetBarrelRecipes();
        if (recipes == null)
        {
            return matches;
        }

        PolymorphMod? mod = withDenied ? null : PolymorphMod.Of(barrel.Api!);

        ItemSlot[] slots = { barrel.Inventory[0], barrel.Inventory[1] };
        foreach (BarrelRecipe recipe in recipes)
        {
            int outSize;
            bool matched = player == null ? recipe.Matches(slots, out outSize) : recipe.Matches(player, slots, out outSize);
            if (matched && mod?.IsDenied(recipe) != true && !matches.Any(match => Key(match.Item1) == Key(recipe)))
            {
                matches.Add((recipe, outSize));
            }
        }

        // With barrel choices turned off, the barrel uses its first match, as in vanilla.
        if (mod != null && !mod.Settings.Barrel && matches.Count > 1)
        {
            matches.RemoveRange(1, matches.Count - 1);
        }

        return matches;
    }

    public static string Key(BarrelRecipe recipe)
    {
        return recipe.Code ?? "";
    }

    // The same set of recipes always gets the same key, whatever the order they match in.
    public static string GroupKey(List<(BarrelRecipe Recipe, int OutSize)> matches)
    {
        return string.Join("|", matches.Select(match => Key(match.Recipe)).OrderBy(code => code, StringComparer.Ordinal));
    }

    public static bool WindowOpen(BlockEntityBarrel barrel)
    {
        return InvDialog(barrel)?.IsOpened() == true;
    }

    public static string? ChoiceOf(BlockEntityBarrel barrel, string group)
    {
        return States.TryGetValue(barrel, out BarrelState? state) && state.Choices.TryGetValue(group, out string? key) ? key : null;
    }

    // True while the barrel holds a mix of recipes and nobody has chosen yet.
    public static bool Waiting(BlockEntityBarrel barrel)
    {
        return States.TryGetValue(barrel, out BarrelState? state) && state.Waiting;
    }

    internal static void SetActor(IPlayer? player)
    {
        _actor = player;
    }

    // Replaces the game's first match when more than one recipe matches, or when a denied one does. Returns false to
    // skip the game's search.
    internal static bool Resolve(BlockEntityBarrel barrel, IPlayer? byPlayer)
    {
        if (barrel.Api == null)
        {
            return true;
        }

        BarrelState state = States.GetOrCreateValue(barrel);
        List<(BarrelRecipe Recipe, int OutSize)> matches = Matches(barrel, byPlayer);
        if (matches.Count < 2 && Matches(barrel, byPlayer, withDenied: true).Count == matches.Count)
        {
            state.Waiting = false;
            return true;
        }

        barrel.CurrentRecipe = null;
        IgnoreChange(barrel) = true;
        bool wait = false;
        int index = 0;
        if (matches.Count >= 2)
        {
            string? key = ChoiceOf(barrel, GroupKey(matches));
            index = key == null ? -1 : matches.FindIndex(match => Key(match.Recipe) == key);

            // A player filling the barrel chooses an instant recipe each time, with the remembered one marked. Only
            // automation uses the remembered one, and not while a player it just asked is still choosing.
            IPlayer? asker = byPlayer ?? _actor;
            long now = barrel.Api.World.ElapsedMilliseconds;
            bool instant = index >= 0 && matches[index].Recipe.SealHours <= 0;
            // A player is asked each time unless the server turned that off. After asking, automation waits before using
            // the remembered choice, so it cannot craft first.
            PolymorphConfig settings = PolymorphMod.SettingsOf(barrel.Api);
            long hold = settings.BarrelAutomationWaitSeconds * 1000L;
            wait = index < 0 || (instant && !_confirmed && ((asker != null && settings.BarrelAskEveryTime) || now - state.AskedAt < hold));
            if (wait && asker is IServerPlayer player)
            {
                state.AskedAt = now;
                PolymorphMod.Of(barrel.Api)?.AskBarrelChoice(player, barrel.Pos);
            }
        }

        // Every match denied: the barrel makes nothing. One left: it is used as the game would use its only match.
        if (matches.Count == 0)
        {
            state.Waiting = false;
        }
        else if (wait)
        {
            state.Waiting = true;
        }
        else
        {
            state.Waiting = false;
            (BarrelRecipe recipe, int outSize) = matches[index];
            if (recipe.SealHours > 0)
            {
                barrel.CurrentRecipe = recipe;
                barrel.CurrentOutSize = outSize;
            }
            else if (barrel.Api.Side == EnumAppSide.Server)
            {
                recipe.TryCraftNow(barrel.Api, 0, new[] { barrel.Inventory[0], barrel.Inventory[1] });
                barrel.MarkDirty(true);
                barrel.Api.World.BlockAccessor.MarkBlockEntityDirty(barrel.Pos);
            }
        }

        // The same bookkeeping the game does after finding a match.
        InvDialog(barrel)?.UpdateContents();
        if (barrel.Api.Side == EnumAppSide.Client)
        {
            CurrentMesh(barrel) = null;
            barrel.MarkDirty(true);
        }

        IgnoreChange(barrel) = false;
        return false;
    }

    public static void OnChoicePacket(IServerPlayer player, BarrelChoicePacket packet)
    {
        var pos = new BlockPos(packet.X, packet.Y, packet.Z, packet.Dimension);
        if (player.Entity?.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityBarrel barrel || barrel.Sealed)
        {
            return;
        }

        // A player may choose for a barrel they could use themselves, from close by.
        if (!player.Entity.World.Claims.TryAccess(player, pos, EnumBlockAccessFlags.Use)
            || player.Entity.Pos.DistanceTo(pos.ToVec3d().Add(0.5, 0.5, 0.5)) > PolymorphMod.SettingsOf(player.Entity.Api).BarrelChooseRange)
        {
            return;
        }

        List<(BarrelRecipe Recipe, int OutSize)> matches = Matches(barrel, player);
        if (matches.Count < 2 || !matches.Any(match => Key(match.Recipe) == packet.Key))
        {
            return;
        }

        BarrelState state = States.GetOrCreateValue(barrel);
        state.Choices[GroupKey(matches)] = packet.Key;
        state.AskedAt = long.MinValue / 2;
        try
        {
            _confirmed = true;
            FindMatchingRecipeMethod.Invoke(barrel, new object?[] { player });
        }
        finally
        {
            _confirmed = false;
        }

        barrel.MarkDirty(true);
    }

    // After the denylist changed: every open barrel window works its recipe out again and shows it.
    public static void RefreshOpenDialogs(ICoreClientAPI capi)
    {
        foreach (GuiDialog dialog in capi.Gui.OpenedGuis.ToList())
        {
            if (dialog is GuiDialogBarrel barrelDialog
                && capi.World.BlockAccessor.GetBlockEntity(barrelDialog.BlockEntityPosition) is BlockEntityBarrel barrel)
            {
                FindMatchingRecipeMethod.Invoke(barrel, new object?[] { capi.World.Player });
                barrelDialog.UpdateContents();
                BarrelChoiceSource.Refresh(barrel);
            }
        }
    }

    public static void Save(BlockEntityBarrel barrel, ITreeAttribute tree)
    {
        if (!States.TryGetValue(barrel, out BarrelState? state) || state.Choices.Count == 0)
        {
            return;
        }

        var choices = new TreeAttribute();
        foreach ((string group, string key) in state.Choices)
        {
            choices.SetString(group, key);
        }

        tree[TreeKey] = choices;
    }

    public static void Load(BlockEntityBarrel barrel, ITreeAttribute tree)
    {
        BarrelState state = States.GetOrCreateValue(barrel);
        state.Choices.Clear();
        if (tree[TreeKey] is ITreeAttribute choices)
        {
            foreach (KeyValuePair<string, IAttribute> entry in choices)
            {
                if (entry.Value.GetValue() is string key)
                {
                    state.Choices[entry.Key] = key;
                }
            }
        }
    }

    private sealed class BarrelState
    {
        public readonly Dictionary<string, string> Choices = new(StringComparer.Ordinal);
        public bool Waiting;
        public long AskedAt = long.MinValue / 2;
    }
}

[ProtoContract]
public class BarrelChoicePacket
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

// Sent to the player who filled a barrel that needs a choice, so their client shows it.
[ProtoContract]
public class BarrelChoiceRequestPacket
{
    [ProtoMember(1)]
    public int X { get; set; }

    [ProtoMember(2)]
    public int Y { get; set; }

    [ProtoMember(3)]
    public int Z { get; set; }

    [ProtoMember(4)]
    public int Dimension { get; set; }
}

[HarmonyPatchCategory(BarrelChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockEntityBarrel), "FindMatchingRecipe", new[] { typeof(IPlayer) })]
public static class BarrelRecipeMatchPatch
{
    public static bool Prefix(BlockEntityBarrel __instance, IPlayer byPlayer)
    {
        return BarrelChoices.Resolve(__instance, byPlayer);
    }
}

// Pouring into the barrel with a bucket happens here, before the barrel knows who did it.
[HarmonyPatchCategory(BarrelChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockBarrel), nameof(BlockBarrel.OnBlockInteractStart))]
public static class BarrelInteractPatch
{
    public static void Prefix(IPlayer byPlayer)
    {
        BarrelChoices.SetActor(byPlayer);
    }

    public static void Finalizer()
    {
        BarrelChoices.SetActor(null);
    }
}

// Items moved in the barrel window arrive here.
[HarmonyPatchCategory(BarrelChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockEntityBarrel), nameof(BlockEntityBarrel.OnReceivedClientPacket))]
public static class BarrelPacketPatch
{
    public static void Prefix(IPlayer player)
    {
        BarrelChoices.SetActor(player);
    }

    public static void Finalizer()
    {
        BarrelChoices.SetActor(null);
    }
}

[HarmonyPatchCategory(BarrelChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockEntityBarrel), nameof(BlockEntityBarrel.ToTreeAttributes))]
public static class BarrelSavePatch
{
    public static void Postfix(BlockEntityBarrel __instance, ITreeAttribute tree)
    {
        BarrelChoices.Save(__instance, tree);
    }
}

// Read before the barrel loads, because loading ends with the barrel looking for its recipe.
[HarmonyPatchCategory(BarrelChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockEntityBarrel), nameof(BlockEntityBarrel.FromTreeAttributes))]
public static class BarrelLoadPatch
{
    public static void Prefix(BlockEntityBarrel __instance, ITreeAttribute tree)
    {
        BarrelChoices.Load(__instance, tree);
    }

    public static void Postfix(BlockEntityBarrel __instance)
    {
        BarrelChoiceSource.Refresh(__instance);
    }
}

// The contents text says when the barrel is waiting for a choice.
[HarmonyPatchCategory(BarrelChoices.PatchCategory)]
[HarmonyPatch(typeof(GuiDialogBarrel), "getContentsText")]
public static class BarrelContentsTextPatch
{
    public static void Postfix(GuiDialogBarrel __instance, ref string __result)
    {
        if (__instance.capiAccess() is ICoreClientAPI capi
            && capi.World.BlockAccessor.GetBlockEntity(__instance.BlockEntityPosition) is BlockEntityBarrel barrel
            && BarrelChoices.Waiting(barrel))
        {
            __result += "\n\n" + Vintagestory.API.Config.Lang.Get("polymorph:barrel-choose");
        }
    }
}

[HarmonyPatchCategory(BarrelChoices.PatchCategory)]
[HarmonyPatch(typeof(GuiDialogBarrel), "SetupDialog")]
public static class BarrelDialogPatch
{
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return SurvivalInventoryPatch.BeforeCompose(instructions, typeof(BarrelDialogPatch), nameof(AddChoiceButton), "the barrel dialog");
    }

    // Smaller than on other windows: the liquid bar starts about 50 to the right of the input slot.
    private const double ButtonSize = 30;

    // Beside the input slot, before the liquid bar.
    private static GuiComposer AddChoiceButton(GuiComposer composer, GuiDialogBarrel dialog)
    {
        GuiElementItemSlotGrid? input = composer.GetSlotGrid("inputSlot");
        if (input == null || dialog.capiAccess() is not ICoreClientAPI capi)
        {
            return composer;
        }

        if (capi.World.BlockAccessor.GetBlockEntity(dialog.BlockEntityPosition) is not BlockEntityBarrel barrel)
        {
            return composer;
        }

        var source = new BarrelChoiceSource(capi, barrel);
        // The barrel's slot bounds are 10 taller than the slot itself, so the button is centered on the 48 high slot.
        ElementBounds slot = input.Bounds;
        ElementBounds bounds = ElementBounds.FixedSize(ButtonSize, ButtonSize);
        bounds.Alignment = slot.Alignment;
        bounds.ParentBounds = slot.ParentBounds;
        bounds.fixedX = slot.fixedX + GuiElementPassiveItemSlot.unscaledSlotSize + 4;
        bounds.fixedY = slot.fixedY + (GuiElementPassiveItemSlot.unscaledSlotSize - ButtonSize) / 2;
        source.Button = PolymorphChoiceButton.AddTo(composer, capi, bounds, source);
        BarrelChoiceSource.Remember(barrel, source);
        return composer;
    }
}

internal sealed class BarrelChoiceSource : IPolymorphChoices
{
    // The button of each barrel's window, so a request from the server can open the panel there.
    private static readonly ConditionalWeakTable<BlockEntityBarrel, BarrelChoiceSource> Sources = new();

    private readonly ICoreClientAPI _capi;
    private readonly BlockEntityBarrel _barrel;
    private string _signature = "";
    private int _count;

    public BarrelChoiceSource(ICoreClientAPI capi, BlockEntityBarrel barrel)
    {
        _capi = capi;
        _barrel = barrel;
    }

    public PolymorphChoiceButton? Button { get; set; }

    public static void Remember(BlockEntityBarrel barrel, BarrelChoiceSource source)
    {
        Sources.AddOrUpdate(barrel, source);
    }

    // Checked every frame, so the matches are only counted again when the contents change.
    public bool HasAlternatives
    {
        get
        {
            string signature = Signature(_barrel);
            if (signature != _signature)
            {
                _signature = signature;
                _count = BarrelChoices.Matches(_barrel, _capi.World.Player).Count;
            }

            return _count > 1;
        }
    }

    public bool Locked => _barrel.Sealed;

    public void ToggleChoices(double renderX, double renderY, double outerWidth, double outerHeight)
    {
        if (PolymorphMod.Of(_capi) is not PolymorphMod mod)
        {
            return;
        }

        (ItemStack?[] outputs, int active, Action<int> onSelected, string?[] labels, string?[] notes) = Panel(mod, _capi, _barrel);
        mod.ToggleChoiceDialog(renderX, renderY, outerWidth, outerHeight, outputs, active, onSelected, _barrel, labels: labels, notes: notes);
    }

    // The server asked this player to choose, for example after they poured into the barrel without opening it.
    public static void Ask(ICoreClientAPI capi, BlockPos pos)
    {
        if (PolymorphMod.Of(capi) is not PolymorphMod mod
            || capi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityBarrel barrel
            || mod.ChoicesOpenFor(barrel))
        {
            return;
        }

        // With the barrel window open, the panel opens at its button, as if it was clicked.
        if (BarrelChoices.WindowOpen(barrel) && Sources.TryGetValue(barrel, out BarrelChoiceSource? source) && source.Button != null)
        {
            source.Button.OpenChoices();
            return;
        }

        (ItemStack?[] outputs, int active, Action<int> onSelected, string?[] labels, string?[] notes) = Panel(mod, capi, barrel);
        if (outputs.Length < 2)
        {
            return;
        }

        // Otherwise it opens on its own in the middle of the screen, freeing the mouse so a result can be clicked.
        mod.ToggleChoiceDialog(capi.Render.FrameWidth / 2.0, capi.Render.FrameHeight / 2.0, 0, 0, outputs, active, onSelected, barrel, below: true, standalone: true,
            caption: Vintagestory.API.Config.Lang.Get("polymorph:choose-result"), labels: labels, notes: notes);
    }

    public static void Refresh(BlockEntityBarrel barrel)
    {
        if (barrel.Api is not ICoreClientAPI capi || PolymorphMod.Of(capi) is not PolymorphMod mod || !mod.ChoicesOpenFor(barrel))
        {
            return;
        }

        (ItemStack?[] outputs, int active, Action<int> onSelected, string?[] labels, string?[] notes) = Panel(mod, capi, barrel);
        if (outputs.Length < 2 || barrel.Sealed)
        {
            mod.CloseChoicesFor(barrel);
            return;
        }

        mod.RefreshChoicesFor(barrel, outputs, active, onSelected, labels, notes);
    }

    // Each result says under it whether it is made now or after sealing, and its tooltip says it in full.
    private static (ItemStack?[] Outputs, int Active, Action<int> OnSelected, string?[] Labels, string?[] Notes) Panel(PolymorphMod mod, ICoreClientAPI capi, BlockEntityBarrel barrel)
    {
        List<(BarrelRecipe Recipe, int OutSize)> matches = BarrelChoices.Matches(barrel, capi.World.Player);
        string? key = matches.Count < 2 ? null : BarrelChoices.ChoiceOf(barrel, BarrelChoices.GroupKey(matches));
        int active = key == null ? -1 : matches.FindIndex(match => BarrelChoices.Key(match.Recipe) == key);
        ItemStack?[] outputs = matches.Select(match => Output(match.Recipe, match.OutSize)).ToArray();
        string?[] labels = matches.Select(match => match.Recipe.SealHours > 0 ? SealTime(capi, match.Recipe) : Vintagestory.API.Config.Lang.Get("polymorph:barrel-now")).ToArray();
        string?[] notes = matches.Select(match => match.Recipe.SealHours > 0
            ? Vintagestory.API.Config.Lang.Get("polymorph:barrel-sealed-note", SealTime(capi, match.Recipe))
            : Vintagestory.API.Config.Lang.Get("polymorph:barrel-now-note")).ToArray();
        return (outputs, active, index =>
        {
            // Sent even for the result already marked: for an instant recipe, picking it is what makes the barrel craft.
            if (!barrel.Sealed)
            {
                mod.SendBarrelChoice(barrel.Pos, BarrelChoices.Key(matches[index].Recipe));
            }
        }, labels, notes);
    }

    // The same wording the barrel window uses for its seal time.
    private static string SealTime(ICoreClientAPI capi, BarrelRecipe recipe)
    {
        return SealTime(capi, recipe.SealHours);
    }

    internal static string SealTime(ICoreClientAPI capi, double hours)
    {
        return hours > 24
            ? Vintagestory.API.Config.Lang.Get("{0} days", Math.Round(hours / capi.World.Calendar.HoursPerDay, 1))
            : Vintagestory.API.Config.Lang.Get("{0} hours", hours);
    }

    private static ItemStack? Output(BarrelRecipe recipe, int outSize)
    {
        ItemStack? output = recipe.RecipeOutput.ResolvedItemStack?.Clone();
        if (output != null && outSize > 0)
        {
            output.StackSize = outSize;
        }

        return output;
    }

    private static string Signature(BlockEntityBarrel barrel)
    {
        return string.Join("|", new[] { barrel.Inventory[0], barrel.Inventory[1] }
            .Select(slot => slot.Itemstack == null ? "-" : slot.Itemstack.Collectible.Code + " x" + slot.Itemstack.StackSize));
    }
}

// Barrel recipes that match the same contents, for /polymorph conflicts. Each recipe's ingredients are put in a test
// barrel the way a real one holds them, items in the input slot and liquids in the liquid slot, and every recipe is
// tried on them. A wildcard or tag ingredient is tried with one real item, so a conflict between rarer items can be missing.
public static class BarrelConflicts
{
    public const string DenyPrefix = "barrel:";

    public static string DenyId(BarrelRecipe recipe)
    {
        return DenyPrefix + BarrelChoices.Key(recipe);
    }

    public static bool IsKnown(ICoreServerAPI api, string entry)
    {
        return api.GetBarrelRecipes()?.Any(recipe => DenyId(recipe).Equals(entry, StringComparison.OrdinalIgnoreCase)) == true;
    }

    // Each barrel content found, as a label and as its two slots (items, liquid), with the recipes it fits.
    public static List<(string Contents, ItemStack?[] Slots, List<BarrelRecipe> Recipes)> Groups(ICoreServerAPI api)
    {
        var groups = new List<(string, ItemStack?[], List<BarrelRecipe>)>();
        List<BarrelRecipe> recipes = api.GetBarrelRecipes()?.Where(recipe => recipe.Enabled && recipe.Ingredients != null).ToList() ?? new List<BarrelRecipe>();
        var inventory = new InventoryGeneric(2, "polymorph-barrel-probe", api);
        ItemSlot[] slots = { inventory[0], inventory[1] };
        var wildcards = new Dictionary<string, ItemStack?>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (BarrelRecipe recipe in recipes)
        {
            if (!Fill(api.World, recipe, slots, wildcards))
            {
                continue;
            }

            List<BarrelRecipe> hits = recipes.Where(other => Fits(other, slots)).ToList();
            if (hits.Select(BarrelChoices.Key).Distinct().Count() >= 2)
            {
                hits.Sort((left, right) => string.Compare(BarrelChoices.Key(left), BarrelChoices.Key(right), StringComparison.Ordinal));
                if (seen.Add(string.Join("|", hits.Select(BarrelChoices.Key))))
                {
                    groups.Add((Label(slots), new[] { slots[0].Itemstack?.Clone(), slots[1].Itemstack?.Clone() }, hits));
                }
            }
        }

        slots[0].Itemstack = null;
        slots[1].Itemstack = null;
        groups.Sort((left, right) => string.Compare(left.Item1, right.Item1, StringComparison.Ordinal));
        return groups;
    }

    public static string Report(List<(string Contents, ItemStack?[] Slots, List<BarrelRecipe> Recipes)> groups, System.Func<BarrelRecipe, bool> isDenied)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("Barrel");
        report.AppendLine();
        report.AppendLine("Each entry is one barrel of contents that more than one barrel recipe can make something from.");
        report.AppendLine("The deny lines go in the same Deny list. A denied recipe never makes anything and does not show in the choices.");
        report.AppendLine("Wildcard and tag ingredients are tried with one item each, so a conflict between rarer items can be missing.");
        report.AppendLine();
        if (groups.Count == 0)
        {
            report.AppendLine("No barrel contents fit more than one recipe.");
            return report.ToString();
        }

        for (int index = 0; index < groups.Count; index++)
        {
            (string contents, _, List<BarrelRecipe> recipes) = groups[index];
            report.Append(index + 1).Append(". ").AppendLine(contents);
            report.AppendLine("  These contents can make:");
            foreach (BarrelRecipe recipe in recipes)
            {
                ItemStack? output = recipe.RecipeOutput.ResolvedItemStack;
                report.Append("  ").Append(BarrelChoices.Key(recipe))
                    .Append(" (").Append(output?.Collectible?.Code?.ToString() ?? "?")
                    .Append(", ").Append(recipe.SealHours > 0 ? recipe.SealHours + " hours sealed" : "instant").Append(')');
                if (isDenied(recipe))
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

    private static bool Fits(BarrelRecipe recipe, ItemSlot[] slots)
    {
        try
        {
            return recipe.Matches(slots, out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Puts one batch of the recipe's ingredients in the test barrel. False when they cannot all be placed.
    private static bool Fill(IWorldAccessor world, BarrelRecipe recipe, ItemSlot[] slots, Dictionary<string, ItemStack?> wildcards)
    {
        slots[0].Itemstack = null;
        slots[1].Itemstack = null;
        foreach (BarrelRecipeIngredient ingredient in recipe.Ingredients!)
        {
            ItemStack? stack = ingredient.Code != null && ingredient.Code.Path.Contains('*')
                ? Wildcard(world, ingredient.Code, wildcards)
                : ingredient.ResolvedItemStack?.Clone();
            if (stack == null)
            {
                return false;
            }

            WaterTightContainableProps? liquid = BlockLiquidContainerBase.GetContainableProps(stack);
            stack.StackSize = liquid != null && ingredient.Litres > 0
                ? Math.Max(1, (int)Math.Round(ingredient.Litres * liquid.ItemsPerLitre))
                : Math.Max(1, ingredient.Quantity);
            ItemSlot slot = liquid != null ? slots[1] : slots[0];
            if (slot.Itemstack != null)
            {
                return false;
            }

            slot.Itemstack = stack;
        }

        return true;
    }

    private static ItemStack? Wildcard(IWorldAccessor world, AssetLocation code, Dictionary<string, ItemStack?> wildcards)
    {
        string key = code.ToString();
        if (!wildcards.TryGetValue(key, out ItemStack? item))
        {
            CollectibleObject? match = world.Collectibles.FirstOrDefault(collectible => collectible?.Code != null && collectible.WildCardMatch(code));
            item = match == null ? null : QuernChoices.StackOf(match);
            wildcards[key] = item;
        }

        return item?.Clone();
    }

    private static string Label(ItemSlot[] slots)
    {
        return string.Join(" + ", slots
            .Where(slot => slot.Itemstack != null)
            .Select(slot => slot.Itemstack!.Collectible.Code + " x" + slot.Itemstack.StackSize));
    }
}
