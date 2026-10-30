using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Polymorph;

// /polymorph conflicts also opens this screen for the admin who ran it, as do /polymorph screen and Ctrl+P: every conflict the file lists, with each result
// as a slot that denies it or allows it again with a click. Save writes the denylist and applies it like a reload.
public partial class PolymorphMod
{
    public const string StationGrid = "grid";
    public const string StationQuern = "quern";
    public const string StationCooking = "cooking";
    public const string StationBarrel = "barrel";

    private PolymorphConflictsDialog? _conflictsDialog;

    // The conflicts found last. Which recipes conflict does not change while the server runs, only whether each result
    // is denied, so they are only looked for again when asked; a denylist change updates them and the file in place.
    private ConflictScan? _scan;

    private sealed class ConflictScan
    {
        public List<ConflictGroup> Grids = new();
        public List<(CollectibleObject Input, List<GrindingProperties> Options)> Querns = new();
        public List<(string Pot, ItemStack[] Stacks, List<CookingRecipe> Recipes)> Pots = new();
        public List<(string Contents, ItemStack?[] Slots, List<BarrelRecipe> Recipes)> Barrels = new();
        public ConflictsPacket Packet = new();
    }

    // Players waiting for the search that is running, to get its result in chat and on the screen.
    private readonly List<IServerPlayer> _waitingForConflicts = new();
    private bool _scanning;
    private bool _disposed;

    // Finding every conflict can take a while in a large modpack, so it runs beside the server instead of stopping it.
    // It only reads recipes and items, which do not change while the server runs, and none of Polymorph's own state.
    // The results then go back to the server's thread, where the denylist is checked, the file is written and the
    // screens are sent. A player who asks while it runs waits for the same search; the console gets the result in the
    // server log.
    private void StartScan(ICoreServerAPI api, IServerPlayer? player)
    {
        if (player != null)
        {
            _serverChannel?.SendPacket(new ConflictsPacket { Loading = true }, player);
            if (!_waitingForConflicts.Contains(player))
            {
                _waitingForConflicts.Add(player);
            }
        }

        if (_scanning)
        {
            return;
        }

        _scanning = true;
        Task.Run(() =>
        {
            ConflictScan? scan = null;
            Exception? failure = null;
            try
            {
                scan = FindConflicts(api);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            api.Event.EnqueueMainThreadTask(() => FinishScan(api, scan, failure), "polymorph-conflicts");
        });
    }

    private static ConflictScan FindConflicts(ICoreServerAPI api)
    {
        List<(CollectibleObject Input, List<GrindingProperties> Options)> querns = Grindables(api)
            .Where(grindable => grindable.Options.Count > 1)
            .OrderBy(grindable => grindable.Input.Code.ToString(), StringComparer.Ordinal)
            .ToList();
        return new ConflictScan { Grids = ConflictGroups(api), Querns = querns, Pots = CookingConflicts.Groups(api), Barrels = BarrelConflicts.Groups(api) };
    }

    // When the search failed, the conflicts found before, if any, are kept. When only the file could not be written,
    // the screen still gets the conflicts. Either way everyone waiting is told in chat.
    private void FinishScan(ICoreServerAPI api, ConflictScan? scan, Exception? failure)
    {
        _scanning = false;
        if (_disposed)
        {
            return;
        }

        string message;
        if (scan == null)
        {
            api.Logger.Error("Polymorph could not find the conflicts: {0}", failure);
            message = Lang.Get("polymorph:conflicts-find-failed", failure?.Message ?? "");
        }
        else
        {
            scan.Packet = BuildConflicts(api, scan.Grids, scan.Querns, scan.Pots, scan.Barrels);
            _scan = scan;
            message = TryWriteConflictsFile(api);
            api.Logger.Notification("Polymorph: {0}", message);
        }

        // A screen with nothing to show closes instead of saying there are no conflicts.
        ConflictsPacket packet = _scan?.Packet ?? new ConflictsPacket { Refused = true };
        foreach (IServerPlayer player in _waitingForConflicts.Where(player => player.ConnectionState != EnumClientState.Offline))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
            _serverChannel?.SendPacket(packet, player);
        }

        _waitingForConflicts.Clear();
    }

    // Writes the file from the conflicts found last, with the denylist as it is now.
    private string TryWriteConflictsFile(ICoreServerAPI api)
    {
        ConflictScan scan = _scan!;
        try
        {
            string path = Path.Combine(api.GetOrCreateDataPath("ModConfig"), ConflictsFile);
            File.WriteAllText(path, ConflictReport(scan.Grids) + QuernReport(scan.Querns) + Environment.NewLine + CookingConflicts.Report(scan.Pots, IsDenied)
                + Environment.NewLine + BarrelConflicts.Report(scan.Barrels, IsDenied));
            return "Wrote " + scan.Grids.Count + " grid, " + scan.Querns.Count + " quern, " + scan.Pots.Count + " cooking and "
                + scan.Barrels.Count + " barrel conflict groups to " + path;
        }
        catch (Exception exception)
        {
            api.Logger.Error("Polymorph could not write the conflicts file: {0}", exception.Message);
            return Lang.Get("polymorph:conflicts-write-failed", exception.Message);
        }
    }

    // The denylist changed: every result found last works out again whether it is denied, the same way the screen
    // does, and the file is written again. Nothing is looked for again.
    private void UpdateConflicts(ICoreServerAPI api)
    {
        if (_scan == null)
        {
            return;
        }

        foreach (ConflictEntryData entry in _scan.Packet.Groups.SelectMany(group => group.Entries))
        {
            entry.Denied = _denied.Contains(entry.DenyId) || entry.Keys.Any(_denied.Contains);
        }

        TryWriteConflictsFile(api);
    }

    // Each group carries what goes in, laid out like its station, and what can come out.
    private ConflictsPacket BuildConflicts(ICoreServerAPI api, List<ConflictGroup> grids, List<(CollectibleObject Input, List<GrindingProperties> Options)> querns,
        List<(string Pot, ItemStack[] Stacks, List<CookingRecipe> Recipes)> pots, List<(string Contents, ItemStack?[] Slots, List<BarrelRecipe> Recipes)> barrels)
    {
        var groups = new List<ConflictGroupData>();
        foreach (ConflictGroup group in grids)
        {
            groups.Add(Group(StationGrid, group.Grid, group.Cells, 3,
                group.Recipes.Select(recipe => Entry(recipe.Output?.ResolvedItemStack, DenyId(recipe), DenyKeys(recipe), IsDenied(recipe), RecipeLabel(recipe)))));
        }

        foreach ((CollectibleObject input, List<GrindingProperties> options) in querns)
        {
            groups.Add(Group(StationQuern, input.Code.ToString(), new ItemStack?[] { QuernChoices.StackOf(input) }, 1,
                options.Select(option => Entry(QuernChoices.Output(option), QuernDenyId(input, option), QuernDenyKeys(input, option), IsDenied(input, option), QuernChoices.Key(option)))));
        }

        // A meal stays in its pot, so it shows as the pot holding that meal, the same preview the firepit's choices show.
        var firedPot = api.World.GetBlock(new AssetLocation("claypot-blue-fired")) as BlockCookingContainer;
        foreach ((string pot, ItemStack[] stacks, List<CookingRecipe> recipes) in pots)
        {
            // Four slots in a row, like the firepit window, with the empty ones left empty.
            ItemStack?[] slots = Enumerable.Range(0, 4).Select(i => i < stacks.Length ? stacks[i] : null).ToArray();
            groups.Add(Group(StationCooking, pot, slots, 4,
                // Denied per pot of ingredients; allowing it again also lifts a whole-recipe line.
                recipes.Select(recipe => Entry(CookedPreview(api, firedPot, stacks, recipe), CookingConflicts.DenyId(recipe, stacks),
                    new[] { CookingConflicts.DenyId(recipe, stacks), CookingConflicts.DenyId(recipe) }, IsDenied(recipe, stacks), "cooking recipe " + CookingChoices.Key(recipe)))));
        }

        foreach ((string contents, ItemStack?[] slots, List<BarrelRecipe> recipes) in barrels)
        {
            groups.Add(Group(StationBarrel, contents, slots, 2,
                recipes.Select(recipe => Entry(recipe.RecipeOutput.ResolvedItemStack, BarrelConflicts.DenyId(recipe), new[] { BarrelConflicts.DenyId(recipe) }, IsDenied(recipe), recipe.SealHours,
                    "barrel recipe " + BarrelChoices.Key(recipe) + (recipe.SealHours > 0 ? ", " + recipe.SealHours + " hours sealed" : ", instant")))));
        }

        return new ConflictsPacket { Groups = groups.ToArray() };
    }

    // Finding every conflict is only done when asked, or the first time after the server started; otherwise the screen
    // gets the conflicts found last right away.
    private void SendConflicts(ICoreServerAPI api, IServerPlayer player, bool rescan)
    {
        if (rescan || _scan == null)
        {
            StartScan(api, player);
            return;
        }

        _serverChannel?.SendPacket(_scan.Packet, player);
    }

    private static ConflictGroupData Group(string station, string contents, ItemStack?[] inputs, int columns, IEnumerable<ConflictEntryData> entries)
    {
        return new ConflictGroupData
        {
            Station = station,
            Contents = contents,
            Inputs = inputs.Select(stack => new ConflictInputData { Stack = stack?.ToBytes() }).ToArray(),
            InputColumns = columns,
            Entries = entries.ToArray()
        };
    }

    private static ConflictEntryData Entry(ItemStack? stack, string denyId, IEnumerable<string> keys, bool denied, string note)
    {
        return Entry(stack, denyId, keys, denied, -1, note);
    }

    private static ConflictEntryData Entry(ItemStack? stack, string denyId, IEnumerable<string> keys, bool denied, double sealHours, string note)
    {
        return new ConflictEntryData { Stack = stack?.ToBytes(), DenyId = denyId, Keys = keys.ToArray(), Denied = denied, Note = note, SealHours = sealHours };
    }

    private static ItemStack? CookedPreview(ICoreServerAPI api, BlockCookingContainer? pot, ItemStack[] stacks, CookingRecipe recipe)
    {
        int servings = 0;
        if (pot == null || !recipe.Matches(stacks, ref servings) || servings <= 0)
        {
            return recipe.CooksInto?.ResolvedItemstack;
        }

        return CookingChoices.Preview(api.World, pot, stacks, recipe, servings);
    }

    // An admin saved changes on the conflicts screen.
    private void OnConflictEdit(IServerPlayer player, ConflictEditPacket packet)
    {
        if (_serverApi == null)
        {
            return;
        }

        if (!player.HasPrivilege(Privilege.controlserver))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("polymorph:conflicts-no-privilege"), EnumChatType.Notification);
            return;
        }

        // Allowing a result again removes every denylist line that denied it, in whichever form it was written.
        var allow = new HashSet<string>(packet.Allow, StringComparer.OrdinalIgnoreCase);
        var deny = Settings.Deny.Where(line => !allow.Contains(line.Trim())).ToList();
        foreach (string line in packet.Deny)
        {
            if (!deny.Contains(line, StringComparer.OrdinalIgnoreCase))
            {
                deny.Add(line);
            }
        }

        Settings.Deny = deny.ToArray();
        _serverApi.StoreModConfig(Settings, ConfigFile);
        ApplyDenyList(_serverApi);
        SendToEveryone(_serverApi);
        // SendToEveryone already sent the new denylist, which the open screen uses to update itself, and the conflicts
        // found last and the file were updated with it; which recipes conflict does not change, so nothing is looked for.
        player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("polymorph:conflicts-saved", deny.Count), EnumChatType.Notification);
    }

    private const string ConflictsHotkey = "polymorphconflicts";

    // Ctrl+P by default: free in the game and its built-in mods, and beside ConfigKit's P for its settings screen.
    // Players can change it in the controls settings. Pressing it opens the screen, or closes it when it is open. The
    // screen opens when the server answers: with the conflicts found last, or with a loading message if it looks again.
    private void RegisterConflictsHotkey(ICoreClientAPI api)
    {
        api.Input.RegisterHotKey(ConflictsHotkey, "Polymorph: open conflicts screen", GlKeys.P, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
        api.Input.SetHotKeyHandler(ConflictsHotkey, _ =>
        {
            if (_conflictsDialog?.IsOpened() == true)
            {
                _conflictsDialog.TryClose();
            }
            else if (!api.World.Player.HasPrivilege(Privilege.controlserver))
            {
                // The server checks again; this only spares a player without the privilege an empty screen.
                api.ShowChatMessage(Lang.Get("polymorph:conflicts-screen-no-privilege"));
            }
            else
            {
                _clientChannel?.SendPacket(new ConflictsRequestPacket());
            }

            return true;
        });
    }

    private TextCommandResult OpenConflictsScreen(ICoreServerAPI api, IServerPlayer? player)
    {
        if (player == null)
        {
            return TextCommandResult.Error(Lang.Get("polymorph:conflicts-player-only"));
        }

        if (_scan == null)
        {
            Working(player, "polymorph:command-finding");
        }

        SendConflicts(api, player, false);
        return TextCommandResult.Success();
    }

    private void OnConflictsRequest(IServerPlayer player, ConflictsRequestPacket packet)
    {
        if (_serverApi == null)
        {
            return;
        }

        // Refused: say why, and close the screen the client may already show as loading.
        if (!player.HasPrivilege(Privilege.controlserver))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("polymorph:conflicts-screen-no-privilege"), EnumChatType.Notification);
            _serverChannel?.SendPacket(new ConflictsPacket { Refused = true }, player);
            return;
        }

        if (packet.Reload)
        {
            List<string> invalid = LoadDenyList(_serverApi);
            SendToEveryone(_serverApi);
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                invalid.Count == 0
                    ? Lang.Get("polymorph:conflicts-reloaded", _denied.Count)
                    : Lang.Get("polymorph:conflicts-reloaded-invalid", _denied.Count, string.Join(", ", invalid)),
                EnumChatType.Notification);

            // The new denylist SendToEveryone sent updates the open screen, and the conflicts found last were updated
            // with it; they are only looked for again with Reload conflicts.
            return;
        }

        SendConflicts(_serverApi, player, packet.Rescan);
    }

    private void OnConflicts(ConflictsPacket packet)
    {
        if (_clientApi == null)
        {
            return;
        }

        _conflictsDialog ??= new PolymorphConflictsDialog(_clientApi, edit => _clientChannel?.SendPacket(edit), request => _clientChannel?.SendPacket(request),
            HasSettingsScreen(_clientApi) ? null : OpenSettings);
        _conflictsDialog.Show(packet);
    }
}

[ProtoContract]
public class ConflictsPacket
{
    [ProtoMember(1)]
    public ConflictGroupData[] Groups { get; set; } = Array.Empty<ConflictGroupData>();

    // Sent first, with no groups, so the screen can say it is loading while the server looks.
    [ProtoMember(2)]
    public bool Loading { get; set; }

    // The server will not show this player the conflicts, or could not find them, so a screen waiting for them closes.
    [ProtoMember(3)]
    public bool Refused { get; set; }
}

[ProtoContract]
public class ConflictGroupData
{
    [ProtoMember(1)]
    public string Station { get; set; } = "";

    // What goes in: the grid, the item ground, the pot or the barrel's contents.
    [ProtoMember(2)]
    public string Contents { get; set; } = "";

    [ProtoMember(3)]
    public ConflictEntryData[] Entries { get; set; } = Array.Empty<ConflictEntryData>();

    // What goes in, row by row, InputColumns wide. An empty slot keeps its place so a grid keeps its shape.
    [ProtoMember(4)]
    public ConflictInputData[] Inputs { get; set; } = Array.Empty<ConflictInputData>();

    [ProtoMember(5)]
    public int InputColumns { get; set; } = 1;
}

[ProtoContract]
public class ConflictInputData
{
    [ProtoMember(1)]
    public byte[]? Stack { get; set; }
}

[ProtoContract]
public class ConflictEntryData
{
    [ProtoMember(1)]
    public byte[]? Stack { get; set; }

    // The line that denies this result.
    [ProtoMember(2)]
    public string DenyId { get; set; } = "";

    // Every line that would deny it, so allowing it again removes the one that was used.
    [ProtoMember(3)]
    public string[] Keys { get; set; } = Array.Empty<string>();

    [ProtoMember(4)]
    public bool Denied { get; set; }

    [ProtoMember(5)]
    public string Note { get; set; } = "";

    // A barrel recipe's seal time, 0 for an instant one, or below 0 for other stations. IsRequired because protobuf
    // leaves out a 0, which would turn back into -1 on the other side and hide an instant recipe's "Now".
    [ProtoMember(6, IsRequired = true)]
    public double SealHours { get; set; } = -1;
}

// Asks the server for the conflicts screen, from the hotkey and the screen's reload buttons.
[ProtoContract]
public class ConflictsRequestPacket
{
    // Only read polymorph.json again and apply it for everyone, as /polymorph reload does.
    [ProtoMember(1)]
    public bool Reload { get; set; }

    // Find the conflicts again and write the file, even though the denylist has not changed.
    [ProtoMember(2)]
    public bool Rescan { get; set; }
}

[ProtoContract]
public class ConflictEditPacket
{
    [ProtoMember(1)]
    public string[] Deny { get; set; } = Array.Empty<string>();

    [ProtoMember(2)]
    public string[] Allow { get; set; } = Array.Empty<string>();
}

internal sealed class PolymorphConflictsDialog : GuiDialog
{
    private const double Width = 720;
    private const double ListHeight = 520;
    private const double SlotPitch = 51;
    private const double CellWidth = 68;
    private const double HeaderHeight = 22;
    private const double ResultRowHeight = 74;

    // A barrel result has one more line under it, for when it is made.
    private const double TimingHeight = 16;
    private const double ArrowWidth = 46;

    private static readonly string[] Stations =
    {
        "", PolymorphMod.StationGrid, PolymorphMod.StationQuern, PolymorphMod.StationCooking, PolymorphMod.StationBarrel
    };

    private readonly Action<ConflictEditPacket> _send;
    private readonly Action<ConflictsRequestPacket> _request;
    private readonly Action? _openSettings;
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

    // Conflicts settled with Shift+click, with the result kept and the unsaved clicks they had before, to go back to.
    private readonly Dictionary<ConflictGroupData, (ConflictEntryData Kept, List<string> Before)> _keptOnly = new();
    private readonly Dictionary<object, ItemStack?> _stacks = new();
    private readonly Dictionary<ConflictGroupData, string> _searchText = new();

    // The page's slots in order: an input (null) or the result it shows.
    private readonly List<ConflictEntryData?> _slotEntries = new();
    private ConflictGroupData[] _groups = Array.Empty<ConflictGroupData>();
    private ChoiceInventory? _inventory;
    private int _station;
    private int _state;
    private int _page;
    private string _search = "";
    private bool _loading;

    // openSettings is null when ConfigKit is installed, and the Settings button is left out.
    public PolymorphConflictsDialog(ICoreClientAPI capi, Action<ConflictEditPacket> send, Action<ConflictsRequestPacket> request, Action? openSettings) : base(capi)
    {
        _send = send;
        _request = request;
        _openSettings = openSettings;
    }

    public override string ToggleKeyCombinationCode => null!;

    public override EnumDialogType DialogType => EnumDialogType.Dialog;

    // Which groups to show by their results' saved state: all, those with a denied result, or those still untouched.
    private static readonly string[] States = { "", "denied", "none" };

    private static string[] StateNames => new[]
    {
        Lang.Get("polymorph:conflicts-state-all"), Lang.Get("polymorph:conflicts-state-denied"), Lang.Get("polymorph:conflicts-state-none")
    };

    private static string[] StationNames => new[]
    {
        Lang.Get("polymorph:conflicts-all"), Lang.Get("polymorph:conflicts-grid"), Lang.Get("polymorph:conflicts-quern"),
        Lang.Get("polymorph:conflicts-cooking"), Lang.Get("polymorph:conflicts-barrel")
    };

    // New conflicts from the server, when the screen opens or after Reload conflicts. Unsaved clicks are kept where they
    // still change something.
    // Opens the screen, or keeps it open, with a loading message in place of the list until the results arrive.
    public void ShowLoading()
    {
        _loading = true;
        Compose();
        if (!IsOpened())
        {
            TryOpen();
        }
    }

    public void Show(ConflictsPacket packet)
    {
        if (packet.Refused)
        {
            _loading = false;
            TryClose();
            return;
        }

        if (packet.Loading)
        {
            ShowLoading();
            return;
        }

        _loading = false;
        Dictionary<string, bool> wanted = WantedStates();
        _groups = packet.Groups;
        _stacks.Clear();
        _searchText.Clear();
        string[] names = StationNames;
        foreach (ConflictGroupData group in _groups)
        {
            var words = new List<string> { names[Math.Max(0, Array.IndexOf(Stations, group.Station))], group.Contents };
            foreach (ConflictInputData input in group.Inputs)
            {
                words.Add(Resolve(input, input.Stack)?.GetName() ?? "");
            }

            foreach (ConflictEntryData entry in group.Entries)
            {
                words.Add(Resolve(entry, entry.Stack)?.GetName() ?? "");
                words.Add(entry.DenyId);
            }

            _searchText[group] = string.Join(" ", words);
        }

        KeepWanted(wanted);
        Compose();
        if (!IsOpened())
        {
            TryOpen();
        }
    }

    private ItemStack? Resolve(object key, byte[]? bytes)
    {
        ItemStack? stack = bytes == null ? null : new ItemStack(bytes);
        stack = stack != null && stack.ResolveBlockOrItem(capi.World) ? stack : null;
        _stacks[key] = stack;
        return stack;
    }

    private bool IsDenied(ConflictEntryData entry)
    {
        return entry.Denied != _changed.Contains(entry.DenyId);
    }

    // A new denylist from the server, after a save here or anywhere. Every result works out its state from the lines
    // that would deny it. Clicks not saved yet keep the state they asked for; after this screen's own save none are left.
    public void ApplyDenylist(IReadOnlyCollection<string> denied)
    {
        if (!IsOpened() || _loading)
        {
            return;
        }

        Dictionary<string, bool> wanted = WantedStates();
        var lines = new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase);
        foreach (ConflictEntryData entry in _groups.SelectMany(group => group.Entries))
        {
            entry.Denied = lines.Contains(entry.DenyId) || entry.Keys.Any(lines.Contains);
        }

        KeepWanted(wanted);
        Compose();
    }

    // What each unsaved click asks for: denied or allowed, by deny line.
    private Dictionary<string, bool> WantedStates()
    {
        var wanted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (ConflictEntryData entry in _groups.SelectMany(group => group.Entries).Where(entry => _changed.Contains(entry.DenyId)))
        {
            wanted[entry.DenyId] = IsDenied(entry);
        }

        return wanted;
    }

    // After the server's states changed, a click stays unsaved only where it still asks for something different.
    private void KeepWanted(Dictionary<string, bool> wanted)
    {
        _keptOnly.Clear();
        _changed.Clear();
        foreach (ConflictEntryData entry in _groups.SelectMany(group => group.Entries))
        {
            if (wanted.TryGetValue(entry.DenyId, out bool state) && state != entry.Denied)
            {
                _changed.Add(entry.DenyId);
            }
        }
    }

    private List<ConflictGroupData> Filtered()
    {
        string station = Stations[_station];
        return _groups
            .Where(group => station.Length == 0 || group.Station == station)
            .Where(group => _state == 0 || group.Entries.Any(entry => entry.Denied) == (_state == 1))
            .Where(group => _search.Length == 0 || _searchText[group].Contains(_search, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static int InputRows(ConflictGroupData group)
    {
        int columns = Math.Max(1, group.InputColumns);
        return Math.Max(1, (group.Inputs.Length + columns - 1) / columns);
    }

    private static double ResultsLeft(ConflictGroupData group)
    {
        return Math.Max(1, group.InputColumns) * SlotPitch + ArrowWidth;
    }

    private static int ResultsPerRow(ConflictGroupData group)
    {
        return Math.Max(1, (int)((Width - ResultsLeft(group)) / CellWidth));
    }

    private static double RowHeight(ConflictGroupData group)
    {
        return ResultRowHeight + (group.Station == PolymorphMod.StationBarrel ? TimingHeight : 0);
    }

    private static double GroupHeight(ConflictGroupData group)
    {
        double inputs = InputRows(group) * SlotPitch;
        int resultRows = (group.Entries.Length + ResultsPerRow(group) - 1) / ResultsPerRow(group);
        return HeaderHeight + Math.Max(inputs, resultRows * RowHeight(group)) + 12;
    }

    // Pages hold as many groups as fit, since a crafting grid is taller than a quern.
    private List<List<ConflictGroupData>> Pages(List<ConflictGroupData> groups)
    {
        var pages = new List<List<ConflictGroupData>> { new() };
        double used = 0;
        foreach (ConflictGroupData group in groups)
        {
            double height = GroupHeight(group);
            if (pages[^1].Count > 0 && used + height > ListHeight)
            {
                pages.Add(new List<ConflictGroupData>());
                used = 0;
            }

            pages[^1].Add(group);
            used += height;
        }

        return pages;
    }

    private void Compose()
    {
        List<ConflictGroupData> groups = Filtered();
        List<List<ConflictGroupData>> pages = Pages(groups);
        _page = Math.Clamp(_page, 0, pages.Count - 1);
        List<ConflictGroupData> page = pages[_page];

        _slotEntries.Clear();
        foreach (ConflictGroupData group in page)
        {
            _slotEntries.AddRange(group.Inputs.Select(_ => (ConflictEntryData?)null));
            _slotEntries.AddRange(group.Entries);
        }

        _inventory = new ChoiceInventory(Math.Max(1, _slotEntries.Count), capi);
        string keepOnly = Lang.Get("polymorph:conflicts-keep-only-tip");
        int fill = 0;
        foreach (ConflictGroupData group in page)
        {
            foreach (ConflictInputData input in group.Inputs)
            {
                _inventory[fill++].Itemstack = _stacks.TryGetValue(input, out ItemStack? stack) ? stack?.Clone() : null;
            }

            foreach (ConflictEntryData entry in group.Entries)
            {
                _inventory[fill].Itemstack = _stacks.TryGetValue(entry, out ItemStack? stack) ? stack?.Clone() : null;
                ((ChoiceSlot)_inventory[fill++]).Note = entry.Note + "\n" + entry.DenyId + "\n" + keepOnly;
            }
        }

        // The hint is measured, so a translation that wraps pushes the list down instead of under it.
        float scale = RuntimeEnv.GUIScale;
        CairoFont hintFont = CairoFont.WhiteDetailText();
        string hint = Lang.Get("polymorph:conflicts-hint");
        double hintHeight = capi.Gui.Text.GetMultilineTextHeight(hintFont, hint, GuiElement.scaled(Width)) / scale;
        double listTop = 62 + hintHeight + 10;
        double pageLine = listTop + ListHeight + 4;
        double bottom = pageLine + 24;

        ElementBounds content = ElementBounds.Fixed(0, 0, Width, bottom + 25);
        ElementBounds background = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        background.BothSizing = ElementSizing.FitToChildren;
        background.WithChildren(content);
        ElementBounds dialog = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        string[] names = StationNames;
        CairoFont header = CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold);
        GuiComposer composer = capi.Gui.CreateCompo("polymorphconflicts", dialog)
            .AddShadedDialogBG(background)
            .AddDialogTitleBar(Lang.Get("polymorph:conflicts-title"), () => TryClose())
            .BeginChildElements(background)
            .AddDropDown(Stations, names, _station, OnStation, ElementBounds.Fixed(0, 30, 160, 25), "station")
            .AddTextInput(ElementBounds.Fixed(170, 30, 200, 25), _ => { }, CairoFont.TextInput(), "search")
            .AddSmallButton(Lang.Get("polymorph:conflicts-find"), OnFind, ElementBounds.Fixed(380, 30, 70, 25))
            .AddDropDown(States, StateNames, _state, OnState, ElementBounds.Fixed(Width - 160, 30, 160, 25), "state")
            .AddStaticText(hint, hintFont, ElementBounds.Fixed(0, 62, Width, hintHeight));

        if (_openSettings != null)
        {
            composer.AddSmallButton(Lang.Get("polymorph:conflicts-settings"), () =>
            {
                _openSettings();
                return true;
            }, ElementBounds.Fixed(460, 30, 90, 25));
        }

        if (_loading)
        {
            composer.AddStaticText(Lang.Get("polymorph:conflicts-loading"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, listTop, Width, 20));
            page = new List<ConflictGroupData>();
        }
        else if (page.Count == 0)
        {
            composer.AddStaticText(Lang.Get("polymorph:conflicts-none"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, listTop, Width, 20));
        }

        CairoFont allowed = CairoFont.WhiteDetailText().WithOrientation(EnumTextOrientation.Center);
        CairoFont denied = CairoFont.WhiteDetailText().WithOrientation(EnumTextOrientation.Center).WithColor(GuiStyle.ErrorTextColor);
        double y = listTop;
        int slot = 0;
        foreach (ConflictGroupData group in page)
        {
            composer.AddStaticText(names[Math.Max(0, Array.IndexOf(Stations, group.Station))], header, ElementBounds.Fixed(0, y, Width, 20));
            double top = y + HeaderHeight;

            // What goes in, laid out like the station.
            int columns = Math.Max(1, group.InputColumns);
            int[] inputSlots = Enumerable.Range(slot, group.Inputs.Length).ToArray();
            composer.AddItemSlotGrid(_inventory, _ => { }, columns, inputSlots,
                ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, top, columns, InputRows(group)), "inputs" + slot);
            slot += group.Inputs.Length;

            // An arrow, then what can come out.
            double inputsWidth = columns * SlotPitch;
            composer.AddStaticCustomDraw(ElementBounds.Fixed(inputsWidth + 6, top + 14, ArrowWidth - 12, 22), DrawArrow);
            double left = ResultsLeft(group);
            int perRow = ResultsPerRow(group);
            for (int i = 0; i < group.Entries.Length; i++, slot++)
            {
                double cellX = left + (i % perRow) * CellWidth;
                double cellY = top + (i / perRow) * RowHeight(group);
                ElementBounds slotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, cellX + (CellWidth - GuiElementPassiveItemSlot.unscaledSlotSize) / 2, cellY, 1, 1);
                composer.AddItemSlotGrid(_inventory, _ => { }, 1, new[] { slot }, slotBounds, "entry" + slot);
                bool isDenied = IsDenied(group.Entries[i]);
                composer.AddStaticText(Lang.Get(isDenied ? "polymorph:conflicts-denied" : "polymorph:conflicts-allowed"), isDenied ? denied : allowed,
                    ElementBounds.Fixed(cellX, cellY + GuiElementPassiveItemSlot.unscaledSlotSize + 4, CellWidth, 16));

                // Barrel results say when they are made, in the same words as the barrel's choices.
                double sealHours = group.Entries[i].SealHours;
                if (sealHours >= 0)
                {
                    string timing = sealHours > 0 ? BarrelChoiceSource.SealTime(capi, sealHours) : Lang.Get("polymorph:barrel-now");
                    composer.AddStaticText(timing, allowed, ElementBounds.Fixed(cellX, cellY + GuiElementPassiveItemSlot.unscaledSlotSize + 4 + TimingHeight, CellWidth, TimingHeight));
                }
            }

            y += GroupHeight(group);
        }

        composer
            .AddStaticText(Lang.Get("polymorph:conflicts-page", _page + 1, pages.Count, groups.Count), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, pageLine, Width, 20))
            .AddSmallButton(Lang.Get("polymorph:conflicts-prev"), () => Turn(-1), ElementBounds.Fixed(0, bottom, 80, 25), EnumButtonStyle.Normal, "prev")
            .AddSmallButton(Lang.Get("polymorph:conflicts-next"), () => Turn(1), ElementBounds.Fixed(86, bottom, 80, 25), EnumButtonStyle.Normal, "next");

        // A button grows to fit its text, so the ones on the right are laid out from the right edge by their real width,
        // and a longer label or translation pushes the others left instead of covering them.
        double right = Width;
        AddRightButton(composer, Lang.Get("polymorph:conflicts-close"), () => TryClose(), 88, bottom, ref right, "close");
        AddRightButton(composer, Lang.Get("polymorph:conflicts-save", _changed.Count), OnSave, 90, bottom, ref right, "save");
        AddRightButton(composer, Lang.Get("polymorph:conflicts-reload"), OnReload, 90, bottom, ref right, "reload");
        AddRightButton(composer, Lang.Get("polymorph:conflicts-rescan"), OnRescan, 90, bottom, ref right, "rescan");
        composer.EndChildElements();

        SingleComposer?.Dispose();
        SingleComposer = composer.Compose();
        SingleComposer.GetTextInput("search")?.SetValue(_search, false);

        // A button that has nothing to do is greyed out and ignores clicks: no page before the first or after the
        // last, nothing to save, and nothing at all while the conflicts are still loading.
        SetEnabled("prev", !_loading && _page > 0);
        SetEnabled("next", !_loading && _page < pages.Count - 1);
        SetEnabled("rescan", !_loading);
        SetEnabled("reload", !_loading);
        SetEnabled("save", !_loading && _changed.Count > 0);
    }

    private void AddRightButton(GuiComposer composer, string text, ActionConsumable onClick, double minWidth, double y, ref double right, string key)
    {
        double width = Math.Max(minWidth, CairoFont.SmallButtonText().GetTextExtents(text).Width / RuntimeEnv.GUIScale + 8);
        right -= width;
        composer.AddSmallButton(text, onClick, ElementBounds.Fixed(right, y, width, 25), EnumButtonStyle.Normal, key);
        right -= 6;
    }

    private void SetEnabled(string key, bool enabled)
    {
        GuiElementTextButton? button = SingleComposer?.GetButton(key);
        if (button != null)
        {
            button.Enabled = enabled;
        }
    }

    // The game's own arrow, as on the quern and the firepit, scaled down to sit between the slots. A static draw gets
    // the whole window to draw on, so it first moves to its own place.
    private void DrawArrow(Cairo.Context ctx, Cairo.ImageSurface surface, ElementBounds bounds)
    {
        ctx.Save();
        Cairo.Matrix matrix = ctx.Matrix;
        matrix.Translate(bounds.drawX, bounds.drawY);
        matrix.Scale(GuiElement.scaled(0.25), GuiElement.scaled(0.25));
        ctx.Matrix = matrix;
        capi.Gui.Icons.DrawArrowRight(ctx, 2.0);
        ctx.Restore();
    }

    private void OnStation(string code, bool selected)
    {
        _station = Math.Max(0, Array.IndexOf(Stations, code));
        _page = 0;
        Compose();
    }

    // Goes by the saved state, not unsaved clicks, so a group does not leave the page while it is being changed.
    private void OnState(string code, bool selected)
    {
        _state = Math.Max(0, Array.IndexOf(States, code));
        _page = 0;
        Compose();
    }

    // The search applies on the button, not per keystroke: rebuilding the screen would take the focus off the text box.
    // It matches item names as well as codes and deny lines.
    private bool OnFind()
    {
        _search = SingleComposer?.GetTextInput("search")?.GetText()?.Trim() ?? "";
        _page = 0;
        Compose();
        return true;
    }

    // The server reads polymorph.json again and applies it for everyone; the new denylist then updates this screen like
    // a save does. The conflicts themselves are not looked for again.
    private bool OnReload()
    {
        _request(new ConflictsRequestPacket { Reload = true });
        return true;
    }

    // The server finds every conflict again, writes the file, and sends them. Unsaved clicks are dropped, so this is
    // also the way to start over.
    private bool OnRescan()
    {
        _keptOnly.Clear();
        _changed.Clear();
        _request(new ConflictsRequestPacket { Rescan = true });
        return true;
    }

    private bool Turn(int by)
    {
        _page += by;
        Compose();
        return true;
    }

    // Only results switch between denied and allowed; the inputs are there to look at.
    public override bool OnMouseClickSlot(ItemSlot slot)
    {
        if (_inventory == null || slot.Inventory != _inventory)
        {
            return false;
        }

        int index = _inventory.GetSlotId(slot);
        if (index < 0 || index >= _slotEntries.Count || _slotEntries[index] is not ConflictEntryData entry)
        {
            return true;
        }

        if (ShiftHeld())
        {
            KeepOnly(entry);
        }
        else if (!_changed.Remove(entry.DenyId))
        {
            _changed.Add(entry.DenyId);
        }

        Compose();
        return true;
    }

    private bool ShiftHeld()
    {
        bool[] keys = capi.Input.KeyboardKeyState;
        return keys[(int)GlKeys.ShiftLeft] || keys[(int)GlKeys.ShiftRight];
    }

    // Shift+click: this result is allowed and every other result of its conflict denied, as unsaved clicks, so a
    // conflict is settled in one click and Save still applies it. Shift+clicking it again puts the conflict back as it
    // was before, unsaved clicks included. When there is nothing to go back to, because the conflict was saved or
    // clicked that way, it goes back to its saved state, and from there to every result allowed.
    private void KeepOnly(ConflictEntryData kept)
    {
        ConflictGroupData? group = _groups.FirstOrDefault(candidate => candidate.Entries.Contains(kept));
        if (group == null)
        {
            return;
        }

        if (!group.Entries.All(entry => IsDenied(entry) == (entry != kept)))
        {
            _keptOnly[group] = (kept, group.Entries.Where(entry => _changed.Contains(entry.DenyId)).Select(entry => entry.DenyId).ToList());
            SetStates(group, entry => entry != kept);
        }
        else if (_keptOnly.TryGetValue(group, out (ConflictEntryData Kept, List<string> Before) before) && before.Kept == kept)
        {
            _keptOnly.Remove(group);
            foreach (ConflictEntryData entry in group.Entries)
            {
                _changed.Remove(entry.DenyId);
            }

            _changed.UnionWith(before.Before);
        }
        else if (group.Entries.Any(entry => _changed.Contains(entry.DenyId)))
        {
            _keptOnly.Remove(group);
            SetStates(group, entry => entry.Denied);
        }
        else
        {
            SetStates(group, _ => false);
        }
    }

    // Unsaved clicks that make each result of the group denied or allowed as asked.
    private void SetStates(ConflictGroupData group, System.Func<ConflictEntryData, bool> denied)
    {
        foreach (ConflictEntryData entry in group.Entries)
        {
            if (entry.Denied == denied(entry))
            {
                _changed.Remove(entry.DenyId);
            }
            else
            {
                _changed.Add(entry.DenyId);
            }
        }
    }

    private bool OnSave()
    {
        if (_changed.Count == 0)
        {
            return true;
        }

        List<ConflictEntryData> changed = _groups.SelectMany(group => group.Entries)
            .Where(entry => _changed.Contains(entry.DenyId))
            .GroupBy(entry => entry.DenyId, StringComparer.OrdinalIgnoreCase)
            .Select(same => same.First())
            .ToList();
        _send(new ConflictEditPacket
        {
            Deny = changed.Where(entry => !entry.Denied).Select(entry => entry.DenyId).ToArray(),
            Allow = changed.Where(entry => entry.Denied).SelectMany(entry => entry.Keys.Append(entry.DenyId)).ToArray()
        });
        return true;
    }

    public override void OnGuiClosed()
    {
        // Unsaved clicks are kept, so pressing Ctrl+P by accident does not lose them; the Save count still shows them
        // when the screen opens again.
        base.OnGuiClosed();
        _inventory = null;
    }
}
