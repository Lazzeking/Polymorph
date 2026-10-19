using HarmonyLib;
using Vintagestory.Common;

namespace Polymorph;

[HarmonyPatch(typeof(InventoryCraftingGrid), "FindMatchingRecipe")]
public static class CraftingGridPatch
{
    public static bool Prefix(InventoryCraftingGrid __instance)
    {
        try
        {
            return !PolymorphMod.TryResolve(__instance);
        }
        catch (System.Exception exception)
        {
            __instance.Api.Logger.Error("Polymorph could not resolve the crafting grid: {0}", exception);
            return true;
        }
    }
}
