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

// A cooking pot cooks the first cooking recipe that matches its ingredients. When several match, each firepit
// remembers which one it cooks. The choice can change during the early part of cooking, like on the quern.
public static class CookingChoices
{
    public const string TreeKey = "polymorphCookingRecipe";

    // The cooking patches are applied apart from the others, so a failure here leaves the rest working.
    public const string PatchCategory = "polymorph-cooking";

    // Share of the cooking time during which the recipe can still change.
    public const float SwitchWindow = 0.25f;

    private static readonly ConditionalWeakTable<BlockEntityFirepit, FirepitState> States = new();

    // The firepit whose pot the game is asking about, set while it checks, cooks or describes the pot.
    [ThreadStatic] private static BlockEntityFirepit? _firepit;

    // Every recipe that fully matches these ingredients in this pot, in the game's order.
    public static List<(CookingRecipe Recipe, int Servings)> Matches(IWorldAccessor world, BlockCookingContainer pot, ItemStack[] stacks)
    {
        var matches = new List<(CookingRecipe, int)>();
        List<CookingRecipe>? recipes = world.Api.GetCookingRecipes();
        if (recipes == null || stacks.Length == 0)
        {
            return matches;
        }

        // A dirty pot only cooks recipes that make something other than food, as in GetMatchingCookingRecipe.
        bool dirty = pot.Attributes?["isDirtyPot"].AsBool() == true;
        foreach (CookingRecipe recipe in recipes)
        {
            if (dirty && (recipe.CooksInto == null || recipe.IsFood))
            {
                continue;
            }

            int servings = 0;
            if (recipe.Matches(stacks, ref servings) && servings > 0)
            {
                matches.Add((recipe, servings));
            }
        }

        return matches;
    }

    public static List<(CookingRecipe Recipe, int Servings)> Matches(BlockEntityFirepit firepit)
    {
        if (firepit.Api == null || firepit.inputSlot.Itemstack?.Collectible is not BlockCookingContainer pot || firepit.Inventory is not ISlotProvider slots)
        {
            return new List<(CookingRecipe, int)>();
        }

        return Matches(firepit.Api.World, pot, pot.GetCookingStacks(slots, clone: false));
    }

    public static string Key(CookingRecipe recipe)
    {
        return recipe.Code ?? "";
    }

    public static string? ChoiceOf(BlockEntityFirepit firepit)
    {
        return States.TryGetValue(firepit, out FirepitState? state) ? state.Key : null;
    }

    public static void SetChoice(BlockEntityFirepit firepit, string? key)
    {
        States.GetOrCreateValue(firepit).Key = key;
    }

    public static int ActiveIndex(List<(CookingRecipe Recipe, int Servings)> matches, string? key)
    {
        return key == null ? 0 : Math.Max(0, matches.FindIndex(match => Key(match.Recipe) == key));
    }

    // Past the early part of cooking, the recipe is set.
    public static bool Locked(BlockEntityFirepit firepit)
    {
        return firepit.inputStackCookingTime > firepit.maxCookingTime() * SwitchWindow;
    }

    // What the pot would hold after cooking this recipe, to show in the result panel: a pot of the meal, or the item it cooks into.
    public static ItemStack? Preview(IWorldAccessor world, BlockCookingContainer pot, ItemStack[] stacks, CookingRecipe recipe, int servings)
    {
        if (recipe.CooksInto != null)
        {
            ItemStack? cooked = recipe.CooksInto.ResolvedItemstack?.Clone();
            if (cooked != null)
            {
                cooked.StackSize *= servings;
            }

            return cooked;
        }

        if (world.GetBlock(pot.CodeWithVariant("type", "cooked")) is not BlockCookedContainer meal)
        {
            return null;
        }

        // The same contents DoSmelt puts in the pot: one serving of each ingredient, cooked where the recipe says so.
        ItemStack[] contents = stacks.Select(stack => stack.Clone()).ToArray();
        for (int i = 0; i < contents.Length; i++)
        {
            contents[i].StackSize /= servings;
            ItemStack? cooked = recipe.GetIngrendientFor(contents[i])?.GetMatchingStack(contents[i])?.CookedStack?.ResolvedItemstack?.Clone();
            if (cooked != null)
            {
                contents[i] = cooked;
            }
        }

        var preview = new ItemStack(meal);
        meal.SetContents(recipe.Code, servings, preview, contents);
        return preview;
    }

    internal static void Begin(IWorldAccessor world, ISlotProvider provider)
    {
        _firepit = provider is InventorySmelting { pos: not null } inventory
            ? world.BlockAccessor.GetBlockEntity(inventory.pos) as BlockEntityFirepit
            : null;
    }

    internal static void End()
    {
        _firepit = null;
    }

    // Swaps the game's first match for the firepit's chosen recipe, when that one matches too.
    internal static void Choose(BlockCookingContainer pot, IWorldAccessor world, ItemStack[] stacks, ref CookingRecipe? result, ref int servings)
    {
        BlockEntityFirepit? firepit = _firepit;
        string? key = firepit == null ? null : ChoiceOf(firepit);
        if (key == null || result == null || servings <= 0 || Key(result) == key)
        {
            return;
        }

        foreach ((CookingRecipe recipe, int chosenServings) in Matches(world, pot, stacks))
        {
            if (Key(recipe) == key)
            {
                result = recipe;
                servings = chosenServings;
                return;
            }
        }
    }

    public static void OnChoicePacket(IServerPlayer player, CookingChoicePacket packet)
    {
        var pos = new BlockPos(packet.X, packet.Y, packet.Z, packet.Dimension);
        if (player.Entity?.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityFirepit firepit)
        {
            return;
        }

        // The server opens the firepit inventory for a player only after checking that they may use it.
        if (!player.InventoryManager.HasInventory(firepit.Inventory) || Locked(firepit))
        {
            return;
        }

        if (!Matches(firepit).Any(match => Key(match.Recipe) == packet.Key))
        {
            return;
        }

        SetChoice(firepit, packet.Key);
        firepit.MarkDirty(true);
    }

    private sealed class FirepitState
    {
        public string? Key;
    }
}

[ProtoContract]
public class CookingChoicePacket
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

[HarmonyPatchCategory(CookingChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockCookingContainer), nameof(BlockCookingContainer.GetMatchingCookingRecipe))]
public static class CookingRecipeMatchPatch
{
    public static void Postfix(BlockCookingContainer __instance, IWorldAccessor world, ItemStack[] stacks, ref CookingRecipe? __result, ref int quantityServings)
    {
        CookingChoices.Choose(__instance, world, stacks, ref __result, ref quantityServings);
    }
}

// The three places that ask for the pot's recipe, each told which firepit it is about.
[HarmonyPatchCategory(CookingChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockCookingContainer), nameof(BlockCookingContainer.CanSmelt))]
public static class CookingCanSmeltPatch
{
    public static void Prefix(IWorldAccessor world, ISlotProvider cookingSlotsProvider)
    {
        CookingChoices.Begin(world, cookingSlotsProvider);
    }

    public static void Finalizer()
    {
        CookingChoices.End();
    }
}

[HarmonyPatchCategory(CookingChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockCookingContainer), nameof(BlockCookingContainer.DoSmelt))]
public static class CookingDoSmeltPatch
{
    public static void Prefix(IWorldAccessor world, ISlotProvider cookingSlotsProvider)
    {
        CookingChoices.Begin(world, cookingSlotsProvider);
    }

    public static void Finalizer()
    {
        CookingChoices.End();
    }
}

[HarmonyPatchCategory(CookingChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockCookingContainer), nameof(BlockCookingContainer.GetOutputText))]
public static class CookingOutputTextPatch
{
    public static void Prefix(IWorldAccessor world, ISlotProvider cookingSlotsProvider)
    {
        CookingChoices.Begin(world, cookingSlotsProvider);
    }

    public static void Finalizer()
    {
        CookingChoices.End();
    }
}

[HarmonyPatchCategory(CookingChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockEntityFirepit), nameof(BlockEntityFirepit.ToTreeAttributes))]
public static class FirepitSavePatch
{
    public static void Postfix(BlockEntityFirepit __instance, ITreeAttribute tree)
    {
        string? key = CookingChoices.ChoiceOf(__instance);
        if (key != null)
        {
            tree.SetString(CookingChoices.TreeKey, key);
        }
    }
}

// The choice is read before the firepit loads, because on the client loading also works out the dialog's output text.
[HarmonyPatchCategory(CookingChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockEntityFirepit), nameof(BlockEntityFirepit.FromTreeAttributes))]
public static class FirepitLoadPatch
{
    private static readonly MethodInfo? SetupDialog = AccessTools.Method(typeof(GuiDialogBlockEntityFirepit), "SetupDialog");

    public static void Prefix(BlockEntityFirepit __instance, ITreeAttribute tree, out bool __state)
    {
        string? key = tree.GetString(CookingChoices.TreeKey);
        __state = key != CookingChoices.ChoiceOf(__instance);
        CookingChoices.SetChoice(__instance, key);
    }

    // The dialog only shows a new output text when a slot changes, so a new recipe choice redraws it the same way.
    public static void Postfix(BlockEntityFirepit __instance, GuiDialogBlockEntityFirepit? ___clientDialog, bool __state)
    {
        if (__state && ___clientDialog?.IsOpened() == true && __instance.Api is ICoreClientAPI capi && SetupDialog != null)
        {
            GuiDialogBlockEntityFirepit dialog = ___clientDialog;
            capi.Event.EnqueueMainThreadTask(() => SetupDialog.Invoke(dialog, Array.Empty<object>()), "polymorph-firepit-text");
        }

        CookingChoiceSource.Refresh(__instance);
    }
}

[HarmonyPatchCategory(CookingChoices.PatchCategory)]
[HarmonyPatch(typeof(GuiDialogBlockEntityFirepit), "SetupDialog")]
public static class FirepitDialogPatch
{
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return SurvivalInventoryPatch.BeforeCompose(instructions, typeof(FirepitDialogPatch), nameof(AddChoiceButton), "the firepit dialog");
    }

    private static GuiComposer AddChoiceButton(GuiComposer composer, GuiDialogBlockEntityFirepit dialog)
    {
        GuiElementItemSlotGrid? input = composer.GetSlotGrid("oreslot");
        GuiElementItemSlotGrid? output = composer.GetSlotGrid("outputslot");
        if (input == null || output == null || dialog.capiAccess() is not ICoreClientAPI capi)
        {
            return composer;
        }

        if (capi.World.BlockAccessor.GetBlockEntity(dialog.BlockEntityPosition) is not BlockEntityFirepit firepit)
        {
            return composer;
        }

        PolymorphChoiceButton.AddTo(composer, capi, PolymorphChoiceButton.BoundsBetween(input.Bounds, output.Bounds), new CookingChoiceSource(capi, firepit));
        return composer;
    }
}

[HarmonyPatchCategory(CookingChoices.PatchCategory)]
[HarmonyPatch(typeof(GuiDialogBlockEntityFirepit), nameof(GuiDialogBlockEntityFirepit.OnGuiClosed))]
public static class FirepitDialogClosePatch
{
    public static void Postfix(GuiDialogBlockEntityFirepit __instance)
    {
        if (__instance.capiAccess() is ICoreClientAPI capi)
        {
            capi.ModLoader.GetModSystem<PolymorphMod>()?.CloseChoices();
        }
    }
}

internal sealed class CookingChoiceSource : IPolymorphChoices
{
    private readonly ICoreClientAPI _capi;
    private readonly BlockEntityFirepit _firepit;
    private string _signature = "";
    private int _count;

    public CookingChoiceSource(ICoreClientAPI capi, BlockEntityFirepit firepit)
    {
        _capi = capi;
        _firepit = firepit;
    }

    // Checked every frame, so the matches are only counted again when the pot or its ingredients change.
    public bool HasAlternatives
    {
        get
        {
            string signature = Signature(_firepit);
            if (signature != _signature)
            {
                _signature = signature;
                _count = CookingChoices.Matches(_firepit).Count;
            }

            return _count > 1;
        }
    }

    public bool Locked => CookingChoices.Locked(_firepit);

    public string LockedLangKey => "polymorph:result-locked-cooking";

    public void ToggleChoices(double renderX, double renderY, double outerWidth, double outerHeight)
    {
        if (PolymorphMod.Of(_capi) is not PolymorphMod mod)
        {
            return;
        }

        (ItemStack?[] outputs, int active, Action<int> onSelected) = Panel(mod, _firepit);
        mod.ToggleChoiceDialog(renderX, renderY, outerWidth, outerHeight, outputs, active, onSelected, _firepit, below: true);
    }

    // Shows the open result panel of this firepit as it is now, or closes it when there is nothing left to pick.
    public static void Refresh(BlockEntityFirepit firepit)
    {
        if (firepit.Api is not ICoreClientAPI capi || PolymorphMod.Of(capi) is not PolymorphMod mod || !mod.ChoicesOpenFor(firepit))
        {
            return;
        }

        (ItemStack?[] outputs, int active, Action<int> onSelected) = Panel(mod, firepit);
        if (outputs.Length < 2 || CookingChoices.Locked(firepit))
        {
            mod.CloseChoicesFor(firepit);
            return;
        }

        mod.RefreshChoicesFor(firepit, outputs, active, onSelected);
    }

    private static (ItemStack?[] Outputs, int Active, Action<int> OnSelected) Panel(PolymorphMod mod, BlockEntityFirepit firepit)
    {
        List<(CookingRecipe Recipe, int Servings)> matches = CookingChoices.Matches(firepit);
        int active = CookingChoices.ActiveIndex(matches, CookingChoices.ChoiceOf(firepit));
        var outputs = new ItemStack?[matches.Count];
        if (firepit.inputSlot.Itemstack?.Collectible is BlockCookingContainer pot && firepit.Inventory is ISlotProvider slots)
        {
            ItemStack[] stacks = pot.GetCookingStacks(slots);
            for (int i = 0; i < matches.Count; i++)
            {
                outputs[i] = CookingChoices.Preview(firepit.Api.World, pot, stacks, matches[i].Recipe, matches[i].Servings);
            }
        }

        return (outputs, active, index =>
        {
            if (index != active && !CookingChoices.Locked(firepit))
            {
                mod.SendCookingChoice(firepit.Pos, CookingChoices.Key(matches[index].Recipe));
            }
        });
    }

    private static string Signature(BlockEntityFirepit firepit)
    {
        if (firepit.Inventory is not ISlotProvider slots)
        {
            return "";
        }

        IEnumerable<ItemSlot> all = slots.Slots.Prepend(firepit.inputSlot);
        return string.Join("|", all.Select(slot => slot.Itemstack == null ? "-" : slot.Itemstack.Collectible.Code + " x" + slot.Itemstack.StackSize));
    }
}
