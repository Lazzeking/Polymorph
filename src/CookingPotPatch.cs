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

    // Every recipe that fully matches these ingredients in this pot and is not denied, in the game's order.
    public static List<(CookingRecipe Recipe, int Servings)> Matches(IWorldAccessor world, BlockCookingContainer pot, ItemStack[] stacks)
    {
        var matches = new List<(CookingRecipe, int)>();
        List<CookingRecipe>? recipes = world.Api.GetCookingRecipes();
        if (recipes == null || stacks.Length == 0)
        {
            return matches;
        }

        PolymorphMod? mod = PolymorphMod.Of(world.Api);

        // A dirty pot only cooks recipes that make something other than food, as in GetMatchingCookingRecipe.
        bool dirty = pot.Attributes?["isDirtyPot"].AsBool() == true;
        foreach (CookingRecipe recipe in recipes)
        {
            if ((dirty && (recipe.CooksInto == null || recipe.IsFood)) || mod?.IsDenied(recipe) == true)
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

    // Swaps the game's first match for the firepit's chosen recipe, when that one matches too. A denied first match
    // gives way to the next allowed one, or to nothing, wherever the game asks.
    internal static void Choose(BlockCookingContainer pot, IWorldAccessor world, ItemStack[] stacks, ref CookingRecipe? result, ref int servings)
    {
        if (result == null)
        {
            return;
        }

        bool denied = PolymorphMod.Of(world.Api)?.IsDenied(result) == true;
        BlockEntityFirepit? firepit = _firepit;
        string? key = firepit == null ? null : ChoiceOf(firepit);
        if (!denied && (key == null || servings <= 0 || Key(result) == key))
        {
            return;
        }

        List<(CookingRecipe Recipe, int Servings)> matches = Matches(world, pot, stacks);
        int index = key == null ? -1 : matches.FindIndex(match => Key(match.Recipe) == key);
        if (index < 0 && denied && matches.Count > 0)
        {
            index = 0;
        }

        if (index >= 0)
        {
            result = matches[index].Recipe;
            servings = matches[index].Servings;
        }
        else if (denied)
        {
            result = null;
            servings = 0;
        }
    }

    private static readonly MethodInfo? SetupDialogMethod = AccessTools.Method(typeof(GuiDialogBlockEntityFirepit), "SetupDialog");
    private static readonly MethodInfo? SetDialogValuesMethod = AccessTools.Method(typeof(BlockEntityFirepit), "SetDialogValues");

    // The firepit window only shows a new output text when a slot changes, so a new choice redraws it the same way.
    public static void Redraw(ICoreClientAPI capi, GuiDialogBlockEntityFirepit dialog)
    {
        if (SetupDialogMethod != null)
        {
            capi.Event.EnqueueMainThreadTask(() => SetupDialogMethod.Invoke(dialog, Array.Empty<object>()), "polymorph-firepit-text");
        }
    }

    // Works the output text of every open firepit window out again and shows it, for example after the denylist changed.
    public static void RedrawOpenDialogs(ICoreClientAPI capi)
    {
        foreach (GuiDialog dialog in capi.Gui.OpenedGuis.ToList())
        {
            if (dialog is GuiDialogBlockEntityFirepit firepitDialog
                && capi.World.BlockAccessor.GetBlockEntity(firepitDialog.BlockEntityPosition) is BlockEntityFirepit firepit)
            {
                SetDialogValuesMethod?.Invoke(firepit, new object[] { firepitDialog.Attributes });
                Redraw(capi, firepitDialog);
                CookingChoiceSource.Refresh(firepit);
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
    public static void Prefix(BlockEntityFirepit __instance, ITreeAttribute tree, out bool __state)
    {
        string? key = tree.GetString(CookingChoices.TreeKey);
        __state = key != CookingChoices.ChoiceOf(__instance);
        CookingChoices.SetChoice(__instance, key);
    }

    public static void Postfix(BlockEntityFirepit __instance, GuiDialogBlockEntityFirepit? ___clientDialog, bool __state)
    {
        if (__state && ___clientDialog?.IsOpened() == true && __instance.Api is ICoreClientAPI capi)
        {
            CookingChoices.Redraw(capi, ___clientDialog);
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

// Cooking recipes that can cook the same pot, for /polymorph conflicts. A recipe takes a range of ingredients,
// so this tries a few real pots per recipe: its required ingredients at their minimum, one serving each, with
// the first valid item and then each other one in turn. It can miss conflicts between rarer combinations.
public static class CookingConflicts
{
    public const string DenyPrefix = "cooking:";

    private const int PotSlots = 4;
    private const int ItemsTriedPerIngredient = 6;

    public static string DenyId(CookingRecipe recipe)
    {
        return DenyPrefix + CookingChoices.Key(recipe);
    }

    public static bool IsKnown(ICoreServerAPI api, string entry)
    {
        return api.GetCookingRecipes()?.Any(recipe => DenyId(recipe).Equals(entry, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static List<(string Pot, List<CookingRecipe> Recipes)> Groups(ICoreServerAPI api)
    {
        var groups = new List<(string, List<CookingRecipe>)>();
        List<CookingRecipe> recipes = api.GetCookingRecipes()?.Where(recipe => recipe.Enabled && recipe.Ingredients != null).ToList() ?? new List<CookingRecipe>();
        var wildcards = new Dictionary<string, ItemStack?>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (CookingRecipe recipe in recipes)
        {
            foreach (ItemStack[] pot in Pots(api.World, recipe, wildcards))
            {
                List<CookingRecipe> hits = recipes.Where(other => Cooks(other, pot)).ToList();
                if (hits.Select(CookingChoices.Key).Distinct().Count() < 2)
                {
                    continue;
                }

                hits.Sort((left, right) => string.Compare(CookingChoices.Key(left), CookingChoices.Key(right), StringComparison.Ordinal));
                if (seen.Add(string.Join("|", hits.Select(CookingChoices.Key))))
                {
                    groups.Add((PotLabel(pot), hits));
                }
            }
        }

        groups.Sort((left, right) => string.Compare(left.Item1, right.Item1, StringComparison.Ordinal));
        return groups;
    }

    public static string Report(List<(string Pot, List<CookingRecipe> Recipes)> groups, System.Func<CookingRecipe, bool> isDenied)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("Cooking");
        report.AppendLine();
        report.AppendLine("Each entry is one pot of ingredients that more than one cooking recipe can cook.");
        report.AppendLine("The deny lines go in the same Deny list. A denied recipe never cooks and does not show in the choices.");
        report.AppendLine("Only a few ingredient combinations per recipe are tried, so a conflict between rarer combinations can be missing.");
        report.AppendLine();
        if (groups.Count == 0)
        {
            report.AppendLine("No pot can cook more than one recipe.");
            return report.ToString();
        }

        for (int index = 0; index < groups.Count; index++)
        {
            (string pot, List<CookingRecipe> recipes) = groups[index];
            report.Append(index + 1).Append(". ").AppendLine(pot);
            report.AppendLine("  This pot can cook:");
            foreach (CookingRecipe recipe in recipes)
            {
                report.Append("  ").Append(CookingChoices.Key(recipe)).Append(" (").Append(OutputLabel(recipe)).Append(')');
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

    private static bool Cooks(CookingRecipe recipe, ItemStack[] pot)
    {
        try
        {
            int servings = 0;
            return recipe.Matches(pot, ref servings) && servings > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<ItemStack[]> Pots(IWorldAccessor world, CookingRecipe recipe, Dictionary<string, ItemStack?> wildcards)
    {
        List<CookingRecipeIngredient> required = recipe.Ingredients!.Where(ingredient => ingredient.MinQuantity > 0).ToList();
        if (required.Count == 0)
        {
            required = recipe.Ingredients!.Take(1).ToList();
        }

        int[] counts = required.Select(ingredient => Math.Max(1, ingredient.MinQuantity)).ToArray();
        if (counts.Sum() > PotSlots)
        {
            yield break;
        }

        List<ItemStack>[] items = required.Select(ingredient => Servings(world, ingredient, wildcards)).ToArray();
        if (items.Any(options => options.Count == 0))
        {
            yield break;
        }

        int[] picks = new int[required.Count];
        yield return Pot(items, picks, counts);
        for (int changed = 0; changed < required.Count; changed++)
        {
            for (int pick = 1; pick < items[changed].Count; pick++)
            {
                picks[changed] = pick;
                yield return Pot(items, picks, counts);
            }

            picks[changed] = 0;
        }
    }

    private static ItemStack[] Pot(List<ItemStack>[] items, int[] picks, int[] counts)
    {
        var pot = new List<ItemStack>();
        for (int i = 0; i < items.Length; i++)
        {
            for (int copy = 0; copy < counts[i]; copy++)
            {
                pot.Add(items[i][picks[i]].Clone());
            }
        }

        return pot.ToArray();
    }

    // One serving of each item this ingredient takes, as many as it is worth trying.
    private static List<ItemStack> Servings(IWorldAccessor world, CookingRecipeIngredient ingredient, Dictionary<string, ItemStack?> wildcards)
    {
        var servings = new List<ItemStack>();
        foreach (CookingRecipeStack valid in ingredient.ValidStacks ?? Array.Empty<CookingRecipeStack>())
        {
            ItemStack? item = valid.Code.Path.Contains('*') ? Wildcard(world, valid.Code, wildcards) : valid.ResolvedItemstack?.Clone();
            if (item == null)
            {
                continue;
            }

            // The amount Matches counts as one serving: the valid stack's size, in litres for a liquid.
            WaterTightContainableProps? liquid = BlockLiquidContainerBase.GetContainableProps(item);
            item.StackSize = liquid == null
                ? Math.Max(1, valid.StackSize)
                : (int)Math.Round(Math.Max(1, valid.StackSize) * liquid.ItemsPerLitre * ingredient.PortionSizeLitres);
            servings.Add(item);
            if (servings.Count >= ItemsTriedPerIngredient)
            {
                break;
            }
        }

        return servings;
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

    private static string PotLabel(ItemStack[] pot)
    {
        return string.Join(" + ", pot
            .GroupBy(stack => stack.Collectible.Code.ToString())
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + " x" + group.Sum(stack => stack.StackSize)));
    }

    private static string OutputLabel(CookingRecipe recipe)
    {
        ItemStack? output = recipe.CooksInto?.ResolvedItemstack;
        return output?.Collectible?.Code == null ? "meal" : "cooks into " + output.Collectible.Code + " x" + output.StackSize;
    }
}
