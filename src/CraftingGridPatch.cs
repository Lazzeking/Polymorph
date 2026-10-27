using HarmonyLib;
using Vintagestory.Common;

namespace Polymorph;

// The game's own search runs first, with whatever other mods changed in it, so every recipe it finds stays
// craftable. Polymorph then adds the other matches and the player's choice on top.
[HarmonyPatch(typeof(InventoryCraftingGrid), "FindMatchingRecipe")]
public static class CraftingGridPatch
{
    public static void Postfix(InventoryCraftingGrid __instance)
    {
        try
        {
            PolymorphMod.Resolve(__instance);
        }
        catch (System.Exception exception)
        {
            __instance.Api.Logger.Error("Polymorph could not resolve the crafting grid: {0}", exception);
        }
    }
}
