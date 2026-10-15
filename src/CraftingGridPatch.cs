using HarmonyLib;
using Vintagestory.Common;

namespace RecipeSelector;

[HarmonyPatch(typeof(InventoryCraftingGrid), "FindMatchingRecipe")]
public static class CraftingGridPatch
{
    public static bool Prefix(InventoryCraftingGrid __instance)
    {
        try
        {
            return !RecipeSelectorMod.TryResolve(__instance);
        }
        catch (System.Exception exception)
        {
            __instance.Api.Logger.Error("Polymorph could not resolve the crafting grid: " + exception);
            return true;
        }
    }
}
