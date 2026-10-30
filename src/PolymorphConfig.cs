using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ProtoBuf;
using Vintagestory.API.Common;

namespace Polymorph;

// ModConfig/polymorph.json. With ConfigKit installed the same settings also get an in-game screen, whose names,
// section titles and descriptions come from lang/en.json (setting-*, section-*, settingdesc-*). Every setting is
// the server's: players get its values when they join and whenever an admin changes them.
// The settings below Deny travel to players in DenyListPacket. IsRequired makes protobuf send false and 0 too, which
// it would otherwise leave out and so turn back into the defaults on the other side.
[ProtoContract]
public class PolymorphConfig
{
    [Category("Denylist")]
    [Description("polymorph:settingdesc-Deny")]
    public string[] Deny = Array.Empty<string>();

    [Category("Stations")]
    [Description("polymorph:settingdesc-CraftingGrid")]
    [ProtoMember(1, IsRequired = true)]
    public bool CraftingGrid = true;

    [Category("Stations")]
    [Description("polymorph:settingdesc-Quern")]
    [ProtoMember(2, IsRequired = true)]
    public bool Quern = true;

    [Category("Stations")]
    [Description("polymorph:settingdesc-CookingPot")]
    [ProtoMember(3, IsRequired = true)]
    public bool CookingPot = true;

    [Category("Stations")]
    [Description("polymorph:settingdesc-Barrel")]
    [ProtoMember(4, IsRequired = true)]
    public bool Barrel = true;

    [Category("Quern")]
    [Description("polymorph:settingdesc-QuernSwitchPercent")]
    [Range(0, 100)]
    [ProtoMember(5, IsRequired = true)]
    public int QuernSwitchPercent = 25;

    [Category("Cooking pot")]
    [Description("polymorph:settingdesc-CookingSwitchPercent")]
    [Range(0, 100)]
    [ProtoMember(6, IsRequired = true)]
    public int CookingSwitchPercent = 25;

    [Category("Barrel")]
    [Description("polymorph:settingdesc-BarrelAskEveryTime")]
    [ProtoMember(7, IsRequired = true)]
    public bool BarrelAskEveryTime = true;

    [Category("Barrel")]
    [Description("polymorph:settingdesc-BarrelAutomationWaitSeconds")]
    [Range(0, 600)]
    [ProtoMember(8, IsRequired = true)]
    public int BarrelAutomationWaitSeconds = 30;

    [Category("Barrel")]
    [Description("polymorph:settingdesc-BarrelChooseRange")]
    [Range(2, 32)]
    [ProtoMember(9, IsRequired = true)]
    public int BarrelChooseRange = 8;

    // Everything but the denylist, which travels and is checked on its own.
    public void CopySettingsFrom(PolymorphConfig other)
    {
        CraftingGrid = other.CraftingGrid;
        Quern = other.Quern;
        CookingPot = other.CookingPot;
        Barrel = other.Barrel;
        QuernSwitchPercent = Math.Clamp(other.QuernSwitchPercent, 0, 100);
        CookingSwitchPercent = Math.Clamp(other.CookingSwitchPercent, 0, 100);
        BarrelAskEveryTime = other.BarrelAskEveryTime;
        BarrelAutomationWaitSeconds = Math.Clamp(other.BarrelAutomationWaitSeconds, 0, 600);
        BarrelChooseRange = Math.Clamp(other.BarrelChooseRange, 2, 32);
    }
}

// ConfigKit is optional, so it is reached by reflection: its documented RegisterManagedConfig signature is frozen and
// looking it up this way is supported. Without ConfigKit, polymorph.json is read as before.
internal static class ConfigKitBridge
{
    private const string SystemName = "ConfigKit.ConfigKitModSystem";

    private static readonly Type[] RegisterSignature =
    {
        typeof(string), typeof(object), typeof(string), typeof(Action), typeof(Action<string>), typeof(Action)
    };

    public static bool Register(ICoreAPI api, PolymorphConfig config, string file, Action onSyncedFromServer, Action<string> onSettingChanged)
    {
        if (!api.ModLoader.IsModEnabled("configkit"))
        {
            return false;
        }

        try
        {
            ModSystem? system = api.ModLoader.GetModSystem(SystemName);
            MethodInfo? register = system?.GetType().GetMethod("RegisterManagedConfig", RegisterSignature);
            if (register == null)
            {
                api.Logger.Warning("Polymorph found ConfigKit but not its RegisterManagedConfig, so its settings have no in-game screen.");
                return false;
            }

            register.Invoke(system, new object?[] { "polymorph", config, file, onSyncedFromServer, onSettingChanged, null });
            return true;
        }
        catch (Exception exception)
        {
            api.Logger.Warning("Polymorph could not register its settings with ConfigKit: {0}", exception.Message);
            return false;
        }
    }
}
