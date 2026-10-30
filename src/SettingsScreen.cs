using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Polymorph;

// Without ConfigKit or Integrated Mod Manager the conflicts screen has a Settings button that opens this small screen
// for everything in polymorph.json but the denylist, which the conflicts screen itself edits. With either of them
// their own screen does this, so the button is left out and there is one place to change them.
public partial class PolymorphMod
{
    private PolymorphSettingsDialog? _settingsDialog;

    // Integrated Mod Manager builds its own screen for polymorph.json from assets/polymorph/config/imm.json, saves the
    // file, then says so with this event on the server. Nothing of it is referenced, so it stays optional.
    private const string ImmSavedEvent = "imm.polymorph";

    // ConfigKit or Integrated Mod Manager already give the settings a screen, so the Settings button is left out.
    private static bool HasSettingsScreen(ICoreAPI api)
    {
        return api.ModLoader.IsModEnabled("configkit") || api.ModLoader.IsModEnabled("integratedmodmanager");
    }

    // An admin saved polymorph.json in Integrated Mod Manager: it is read again and applied for everyone, the same as
    // /polymorph reload.
    private void OnImmSaved(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (_serverApi != null)
        {
            LoadDenyList(_serverApi);
            SendToEveryone(_serverApi);
        }
    }

    private void OpenSettings()
    {
        if (_clientApi == null)
        {
            return;
        }

        _settingsDialog ??= new PolymorphSettingsDialog(_clientApi, settings => _clientChannel?.SendPacket(new SettingsEditPacket { Settings = settings }));
        _settingsDialog.Show(Settings);
    }

    // An admin saved the settings screen: the values are checked the same way as from polymorph.json, stored with the
    // denylist, and sent to everyone, the same as a ConfigKit change.
    private void OnSettingsEdit(IServerPlayer player, SettingsEditPacket packet)
    {
        if (_serverApi == null || packet.Settings == null)
        {
            return;
        }

        if (!player.HasPrivilege(Privilege.controlserver))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("polymorph:settings-no-privilege"), EnumChatType.Notification);
            return;
        }

        Settings.CopySettingsFrom(packet.Settings);
        _serverApi.StoreModConfig(Settings, ConfigFile);
        SendToEveryone(_serverApi);
        player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("polymorph:settings-saved"), EnumChatType.Notification);
    }
}

// Only the settings travel: the denylist is not a ProtoMember, so it stays as it is on the server.
[ProtoContract]
public class SettingsEditPacket
{
    [ProtoMember(1)]
    public PolymorphConfig? Settings { get; set; }
}

internal sealed class PolymorphSettingsDialog : GuiDialog
{
    private const double Width = 420;
    private const double SwitchRow = 34;
    private const double SliderRow = 52;
    private const double SectionGap = 8;

    private readonly Action<PolymorphConfig> _save;

    // What the screen shows: the server's values, with the changes not saved yet.
    private readonly PolymorphConfig _values = new();
    private bool _changed;

    // Switches and sliders only take their values once the screen is composed.
    private readonly List<(string Setting, bool On)> _pendingSwitches = new();
    private readonly List<(string Setting, int Value, int Min, int Max, int Step, string Unit)> _pendingSliders = new();

    public PolymorphSettingsDialog(ICoreClientAPI capi, Action<PolymorphConfig> save) : base(capi)
    {
        _save = save;
    }

    public override string ToggleKeyCombinationCode => null!;

    public override EnumDialogType DialogType => EnumDialogType.Dialog;

    public void Show(PolymorphConfig server)
    {
        _values.CopySettingsFrom(server);
        _changed = false;
        Compose();
        if (!IsOpened())
        {
            TryOpen();
        }
    }

    // New values from the server, after a save here or anywhere. Changes not saved yet are kept over them.
    public void ApplySettings(PolymorphConfig server)
    {
        if (IsOpened() && !_changed)
        {
            Show(server);
        }
    }

    private void Compose()
    {
        _pendingSwitches.Clear();
        _pendingSliders.Clear();
        ElementBounds background = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        background.BothSizing = ElementSizing.FitToChildren;
        ElementBounds dialog = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        GuiComposer composer = capi.Gui.CreateCompo("polymorphsettings", dialog)
            .AddShadedDialogBG(background)
            .AddDialogTitleBar(Lang.Get("polymorph:settings-title"), () => TryClose())
            .BeginChildElements(background);

        double y = 30;
        Section(composer, "section-Stations", ref y);
        Switch(composer, nameof(PolymorphConfig.CraftingGrid), _values.CraftingGrid, on => _values.CraftingGrid = on, ref y);
        Switch(composer, nameof(PolymorphConfig.Quern), _values.Quern, on => _values.Quern = on, ref y);
        Switch(composer, nameof(PolymorphConfig.CookingPot), _values.CookingPot, on => _values.CookingPot = on, ref y);
        Switch(composer, nameof(PolymorphConfig.Barrel), _values.Barrel, on => _values.Barrel = on, ref y);

        Section(composer, "section-Quern", ref y);
        Slider(composer, nameof(PolymorphConfig.QuernSwitchPercent), _values.QuernSwitchPercent, 0, 100, 1, "%", value => _values.QuernSwitchPercent = value, ref y);

        Section(composer, "section-Cooking pot", ref y);
        Slider(composer, nameof(PolymorphConfig.CookingSwitchPercent), _values.CookingSwitchPercent, 0, 100, 1, "%", value => _values.CookingSwitchPercent = value, ref y);

        Section(composer, "section-Barrel", ref y);
        Switch(composer, nameof(PolymorphConfig.BarrelAskEveryTime), _values.BarrelAskEveryTime, on => _values.BarrelAskEveryTime = on, ref y);
        Slider(composer, nameof(PolymorphConfig.BarrelAutomationWaitSeconds), _values.BarrelAutomationWaitSeconds, 0, 600, 1, "s", value => _values.BarrelAutomationWaitSeconds = value, ref y);
        Slider(composer, nameof(PolymorphConfig.BarrelChooseRange), _values.BarrelChooseRange, 2, 32, 1, "", value => _values.BarrelChooseRange = value, ref y);

        y += SectionGap;
        composer
            .AddSmallButton(Lang.Get("polymorph:settings-save"), OnSave, ElementBounds.Fixed(Width - 184, y, 90, 25), EnumButtonStyle.Normal, "save")
            .AddSmallButton(Lang.Get("polymorph:conflicts-close"), () => TryClose(), ElementBounds.Fixed(Width - 88, y, 88, 25))
            .AddStaticText(Lang.Get("polymorph:settings-hint"), CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y + 4, Width - 190, 20))
            .EndChildElements();

        SingleComposer?.Dispose();
        SingleComposer = composer.Compose();
        foreach ((string setting, bool on) in _pendingSwitches)
        {
            SingleComposer.GetSwitch("switch-" + setting).SetValue(on);
        }

        foreach ((string setting, int value, int min, int max, int step, string unit) in _pendingSliders)
        {
            SingleComposer.GetSlider("slider-" + setting).SetValues(value, min, max, step, unit);
        }

        UpdateSave();
    }

    private void Section(GuiComposer composer, string key, ref double y)
    {
        y += SectionGap;
        composer.AddStaticText(Lang.Get("polymorph:" + key), CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold), ElementBounds.Fixed(0, y, Width, 20));
        y += 24;
    }

    // The setting's name, with its description when hovered, the same texts ConfigKit shows.
    private void Label(GuiComposer composer, string setting, ElementBounds bounds)
    {
        composer
            .AddStaticText(Lang.Get("polymorph:setting-" + setting), CairoFont.WhiteSmallText(), bounds)
            .AddHoverText(Lang.Get("polymorph:settingdesc-" + setting), CairoFont.WhiteSmallText(), 300, bounds.FlatCopy());
    }

    private void Switch(GuiComposer composer, string setting, bool value, Action<bool> set, ref double y)
    {
        composer.AddSwitch(on =>
        {
            set(on);
            Changed();
        }, ElementBounds.Fixed(0, y, 30, 30), "switch-" + setting);
        Label(composer, setting, ElementBounds.Fixed(42, y + 6, Width - 42, 20));
        _pendingSwitches.Add((setting, value));
        y += SwitchRow;
    }

    private void Slider(GuiComposer composer, string setting, int value, int min, int max, int step, string unit, Action<int> set, ref double y)
    {
        Label(composer, setting, ElementBounds.Fixed(0, y, Width, 20));
        composer.AddSlider(newValue =>
        {
            set(newValue);
            Changed();
            return true;
        }, ElementBounds.Fixed(0, y + 22, Width, 20), "slider-" + setting);
        _pendingSliders.Add((setting, value, min, max, step, unit));
        y += SliderRow;
    }

    private void Changed()
    {
        _changed = true;
        UpdateSave();
    }

    private void UpdateSave()
    {
        GuiElementTextButton? save = SingleComposer?.GetButton("save");
        if (save != null)
        {
            save.Enabled = _changed;
        }
    }

    private bool OnSave()
    {
        if (!_changed)
        {
            return true;
        }

        // The server's answer, with the values it accepted, then refreshes the screen.
        _changed = false;
        UpdateSave();
        var settings = new PolymorphConfig();
        settings.CopySettingsFrom(_values);
        _save(settings);
        return true;
    }
}
