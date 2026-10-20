using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Polymorph;

// A quern turns an item into its one grindingProps.groundStack. When mods add more results to an item,
// GrindingOverloads keeps them all, and each quern remembers which one it makes. The choice can change
// during the early part of grinding each item.
public static class QuernChoices
{
    public const string TreeKey = "polymorphGroundKey";

    // The quern patches are applied apart from the grid ones, so a failure here leaves the grid working.
    public const string PatchCategory = "polymorph-quern";

    // The handbook patches go apart again, so a handbook another mod changed does not cost the quern its choices.
    public const string HandbookPatchCategory = "polymorph-quern-handbook";

    // Share of an item's grinding time during which its result can still change.
    public const float SwitchWindow = 0.25f;

    private static ConditionalWeakTable<CollectibleObject, GrindingProperties[]> Extras = new();
    private static readonly ConditionalWeakTable<BlockEntityQuern, QuernState> States = new();

    // The results this item may grind into: the native one first, then the extra ones, without repeats or denied ones.
    public static List<GrindingProperties> Options(IWorldAccessor world, ItemStack? stack)
    {
        List<GrindingProperties> options = AllOptions(world, stack);
        PolymorphMod? mod = world.Api == null ? null : PolymorphMod.Of(world.Api);
        if (stack != null && mod != null)
        {
            options.RemoveAll(option => mod.IsDenied(stack.Collectible, option));
        }

        return options;
    }

    // Every result, denied or not.
    public static List<GrindingProperties> AllOptions(IWorldAccessor world, ItemStack? stack)
    {
        var options = new List<GrindingProperties>();
        if (stack == null)
        {
            return options;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        GrindingProperties? native = stack.Collectible.GetGrindingProperties(world, stack);
        if (native != null && seen.Add(Key(native)))
        {
            options.Add(native);
        }

        foreach (GrindingProperties extra in ExtrasFor(world, stack.Collectible))
        {
            if (seen.Add(Key(extra)))
            {
                options.Add(extra);
            }
        }

        return options;
    }

    // The results kept from mods that added over this item's result.
    public static GrindingProperties[] ExtrasFor(IWorldAccessor world, CollectibleObject collectible)
    {
        return Extras.GetValue(collectible, item => GrindingOverloads.Resolve(world, item).ToArray());
    }

    public static bool AnyExtras(IWorldAccessor world)
    {
        return world.Api != null && GrindingOverloads.For(world.Api).Count > 0;
    }

    // Forgets the resolved extra results, for example when the server sends the kept ones.
    public static void Reset()
    {
        Extras = new ConditionalWeakTable<CollectibleObject, GrindingProperties[]>();
    }

    public static ItemStack? Output(GrindingProperties props)
    {
        return props.GroundStack?.ResolvedItemstack;
    }

    public static string Key(GrindingProperties props)
    {
        ItemStack? stack = Output(props);
        return stack?.Collectible?.Code == null ? "" : stack.Collectible.Code + " x" + stack.StackSize;
    }

    public static string? ChoiceOf(BlockEntityQuern quern)
    {
        return States.TryGetValue(quern, out QuernState? state) ? state.Key : null;
    }

    public static void SetChoice(BlockEntityQuern quern, string? key)
    {
        States.GetOrCreateValue(quern).Key = key;
    }

    public static int ActiveIndex(List<GrindingProperties> options, string? key)
    {
        return key == null ? 0 : Math.Max(0, options.FindIndex(option => Key(option) == key));
    }

    // Past the early part of grinding the current item, its result is set.
    public static bool Locked(BlockEntityQuern quern)
    {
        return quern.inputGrindTime > quern.maxGrindingTime() * SwitchWindow;
    }

    public static void OnChoicePacket(IServerPlayer player, QuernChoicePacket packet)
    {
        var pos = new BlockPos(packet.X, packet.Y, packet.Z, packet.Dimension);
        if (player.Entity?.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityQuern quern)
        {
            return;
        }

        // The server opens the quern inventory for a player only after checking that they may use it.
        if (!player.InventoryManager.HasInventory(quern.Inventory) || Locked(quern))
        {
            return;
        }

        List<GrindingProperties> options = Options(quern.Api.World, quern.InputSlot.Itemstack);
        if (!options.Any(option => Key(option) == packet.Key))
        {
            return;
        }

        SetChoice(quern, packet.Key);
        quern.MarkDirty(true);
    }

    public static ItemStack StackOf(CollectibleObject collectible)
    {
        return collectible is Block block ? new ItemStack(block) : new ItemStack((Item)collectible);
    }

    private sealed class QuernState
    {
        public string? Key;
    }
}

// While the handbook builds a page, the answer comes from GrindingHandbook.
[HarmonyPatchCategory(QuernChoices.HandbookPatchCategory)]
[HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetGrindingProperties))]
public static class GrindingPropertiesPatch
{
    public static void Postfix(IWorldAccessor world, ItemStack itemstack, ref GrindingProperties? __result)
    {
        if (world != null && GrindingHandbook.Current != GrindingHandbook.Mode.None)
        {
            __result = GrindingHandbook.Resolve(world, itemstack, __result);
        }
    }
}

// Rewrites the vanilla "When ground" line to the first allowed result and how many more there are.
[HarmonyPatchCategory(QuernChoices.PatchCategory)]
[HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetHeldItemInfo))]
public static class GrindingTooltipPatch
{
    private const string VanillaLine = "When ground: Turns into {0}x {1}";

    public static void Postfix(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
    {
        ItemStack? stack = inSlot?.Itemstack;
        if (stack == null || world == null)
        {
            return;
        }

        ItemStack? native = stack.Collectible.GetGrindingProperties(world, stack)?.GroundStack?.ResolvedItemstack;
        if (native == null)
        {
            return;
        }

        string vanilla = Lang.Get(VanillaLine, native.StackSize, native.GetName());
        List<GrindingProperties> options = QuernChoices.Options(world, stack);
        ItemStack? first = options.Count == 0 ? null : QuernChoices.Output(options[0]);
        string line = first == null
            ? ""
            : options.Count > 1
                ? Lang.Get("polymorph:ground-into-more", first.StackSize, first.GetName(), options.Count - 1)
                : Lang.Get(VanillaLine, first.StackSize, first.GetName());
        if (line == vanilla)
        {
            return;
        }

        dsc.Replace(vanilla + Environment.NewLine, line.Length == 0 ? "" : line + Environment.NewLine);
    }
}

// Picks the quern's chosen result, or the first allowed one. Nothing grinds when every result is denied.
[HarmonyPatchCategory(QuernChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockEntityQuern), nameof(BlockEntityQuern.InputGrindProps), MethodType.Getter)]
public static class QuernGrindPropsPatch
{
    public static void Postfix(BlockEntityQuern __instance, ref GrindingProperties? __result)
    {
        if (__result == null || __instance.Api == null)
        {
            return;
        }

        List<GrindingProperties> options = QuernChoices.Options(__instance.Api.World, __instance.InputSlot.Itemstack);
        if (options.Count == 0)
        {
            __result = null;
            return;
        }

        string? key = QuernChoices.ChoiceOf(__instance);
        __result = (key == null ? null : options.Find(option => QuernChoices.Key(option) == key)) ?? options[0];
    }
}

[HarmonyPatchCategory(QuernChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockEntityQuern), nameof(BlockEntityQuern.ToTreeAttributes))]
public static class QuernSavePatch
{
    public static void Postfix(BlockEntityQuern __instance, ITreeAttribute tree)
    {
        string? key = QuernChoices.ChoiceOf(__instance);
        if (key != null)
        {
            tree.SetString(QuernChoices.TreeKey, key);
        }
    }
}

// Every server update of the quern lands here on the client, so an open result panel is refreshed from it.
[HarmonyPatchCategory(QuernChoices.PatchCategory)]
[HarmonyPatch(typeof(BlockEntityQuern), nameof(BlockEntityQuern.FromTreeAttributes))]
public static class QuernLoadPatch
{
    public static void Postfix(BlockEntityQuern __instance, ITreeAttribute tree)
    {
        QuernChoices.SetChoice(__instance, tree.GetString(QuernChoices.TreeKey));
        QuernChoiceSource.Refresh(__instance);
    }
}

[HarmonyPatchCategory(QuernChoices.PatchCategory)]
[HarmonyPatch(typeof(GuiDialogBlockEntityQuern), "SetupDialog")]
public static class QuernDialogPatch
{
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return SurvivalInventoryPatch.BeforeCompose(instructions, typeof(QuernDialogPatch), nameof(AddChoiceButton), "the quern dialog");
    }

    // The progress arrow's lower edge sits about 32 below the top of the slots.
    private const double BelowArrow = 34;

    // Shifts the button left of center, clear of the arrow head.
    private const double LeftOfCenter = 10;

    private static GuiComposer AddChoiceButton(GuiComposer composer, GuiDialogBlockEntityQuern dialog)
    {
        GuiElementItemSlotGrid? input = composer.GetSlotGrid("inputSlot");
        GuiElementItemSlotGrid? output = composer.GetSlotGrid("outputslot");
        if (input == null || output == null || dialog.capiAccess() is not ICoreClientAPI capi || !QuernChoices.AnyExtras(capi.World))
        {
            return composer;
        }

        if (capi.World.BlockAccessor.GetBlockEntity(dialog.BlockEntityPosition) is not BlockEntityQuern quern)
        {
            return composer;
        }

        // Centered between the two slots, just under the progress arrow. It reaches into the window's bottom padding
        // without being a child of the padded panel, so the window keeps its vanilla size.
        ElementBounds inputSlot = input.Bounds;
        ElementBounds outputSlot = output.Bounds;
        double size = (outputSlot.fixedHeight > 0 ? outputSlot.fixedHeight : 48) * PolymorphChoiceButton.SizeRatio;
        ElementBounds bounds = ElementBounds.FixedSize(size, size);
        bounds.Alignment = outputSlot.Alignment;
        bounds.ParentBounds = outputSlot.ParentBounds;
        bounds.fixedX = (inputSlot.fixedX + inputSlot.fixedWidth + outputSlot.fixedX - size) / 2 - LeftOfCenter;
        bounds.fixedY = outputSlot.fixedY + BelowArrow;
        PolymorphChoiceButton.AddTo(composer, capi, bounds, new QuernChoiceSource(capi, quern));
        return composer;
    }
}

[HarmonyPatchCategory(QuernChoices.PatchCategory)]
[HarmonyPatch(typeof(GuiDialogBlockEntityQuern), nameof(GuiDialogBlockEntityQuern.OnGuiClosed))]
public static class QuernDialogClosePatch
{
    public static void Postfix(GuiDialogBlockEntityQuern __instance)
    {
        if (__instance.capiAccess() is ICoreClientAPI capi)
        {
            capi.ModLoader.GetModSystem<PolymorphMod>()?.CloseChoices();
        }
    }
}

internal sealed class QuernChoiceSource : IPolymorphChoices
{
    private readonly ICoreClientAPI _capi;
    private readonly BlockEntityQuern _quern;

    public QuernChoiceSource(ICoreClientAPI capi, BlockEntityQuern quern)
    {
        _capi = capi;
        _quern = quern;
    }

    public bool HasAlternatives => QuernChoices.Options(_capi.World, _quern.InputSlot.Itemstack).Count > 1;

    public bool Locked => QuernChoices.Locked(_quern);

    public void ToggleChoices(double renderX, double renderY, double outerWidth, double outerHeight)
    {
        if (PolymorphMod.Of(_capi) is not PolymorphMod mod)
        {
            return;
        }

        (ItemStack?[] outputs, int active, Action<int> onSelected) = Panel(mod, _quern);
        mod.ToggleChoiceDialog(renderX, renderY, outerWidth, outerHeight, outputs, active, onSelected, _quern, below: true);
    }

    // Shows the open result panel of this quern as it is now, or closes it when there is nothing left to pick.
    public static void Refresh(BlockEntityQuern quern)
    {
        if (quern.Api is not ICoreClientAPI capi || PolymorphMod.Of(capi) is not PolymorphMod mod || !mod.ChoicesOpenFor(quern))
        {
            return;
        }

        (ItemStack?[] outputs, int active, Action<int> onSelected) = Panel(mod, quern);
        if (outputs.Length < 2 || QuernChoices.Locked(quern))
        {
            mod.CloseChoicesFor(quern);
            return;
        }

        mod.RefreshChoicesFor(quern, outputs, active, onSelected);
    }

    private static (ItemStack?[] Outputs, int Active, Action<int> OnSelected) Panel(PolymorphMod mod, BlockEntityQuern quern)
    {
        List<GrindingProperties> options = QuernChoices.Options(quern.Api.World, quern.InputSlot.Itemstack);
        int active = QuernChoices.ActiveIndex(options, QuernChoices.ChoiceOf(quern));
        ItemStack?[] outputs = options.Select(QuernChoices.Output).ToArray();
        return (outputs, active, index =>
        {
            if (index != active && !QuernChoices.Locked(quern))
            {
                mod.SendQuernChoice(quern.Pos, QuernChoices.Key(options[index]));
            }
        });
    }
}
