using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Polymorph;

// The handbook asks each item for its one grinding result. While it builds a page, GetGrindingProperties
// answers for that page instead: nothing where Polymorph writes the grinding section itself, or the
// result matching the page when it looks for what grinds into this item. Denied results never show.
public static class GrindingHandbook
{
    public enum Mode
    {
        None,
        Hide,
        MatchPage
    }

    [ThreadStatic] public static Mode Current;
    [ThreadStatic] public static ItemStack? Page;

    public static GrindingProperties? Resolve(IWorldAccessor world, ItemStack? itemstack, GrindingProperties? result)
    {
        Mode mode = Current;
        if (mode == Mode.Hide)
        {
            return null;
        }

        if (mode != Mode.MatchPage || itemstack == null || Page == null)
        {
            return result;
        }

        // Options asks GetGrindingProperties again, which must give the plain answer.
        Current = Mode.None;
        try
        {
            List<GrindingProperties> options = QuernChoices.Options(world, itemstack);
            return options.Find(option => QuernChoices.Output(option)?.Equals(world, Page, GlobalConstants.IgnoredStackAttributes) == true)
                ?? options.FirstOrDefault();
        }
        finally
        {
            Current = mode;
        }
    }

    public static void Reset()
    {
        Current = Mode.None;
        Page = null;
    }

    public static List<ItemStack> Outputs(IWorldAccessor world, ItemStack stack)
    {
        return QuernChoices.Options(world, stack)
            .Select(QuernChoices.Output)
            .OfType<ItemStack>()
            .Where(output => !output.Equals(world, stack, GlobalConstants.IgnoredStackAttributes))
            .ToList();
    }

    public static Action<ItemStack> OpenPage(ICoreClientAPI capi, ActionConsumable<string> openDetailPageFor)
    {
        return stack => openDetailPageFor(
            stack.Collectible.GetCollectibleInterface<IHandBookPageCodeProvider>()?.HandbookPageCodeForStack(capi.World, stack)
            ?? GuiHandbookItemStackPage.PageCodeForStack(stack));
    }

    // Shows the open handbook page again, for example after the denylist changed.
    public static void ReloadOpenPages(ICoreClientAPI capi)
    {
        foreach (GuiDialog dialog in capi.Gui.OpenedGuis.ToList())
        {
            (dialog as GuiDialogHandbook)?.ReloadPage();
        }
    }
}

// The item's own page: "Grinds into" lists every allowed result.
[HarmonyPatchCategory(QuernChoices.HandbookPatchCategory)]
[HarmonyPatch(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "addProcessesIntoInfo")]
public static class HandbookGrindsIntoPatch
{
    public static void Prefix()
    {
        GrindingHandbook.Current = GrindingHandbook.Mode.Hide;
    }

    public static void Postfix(ICoreClientAPI capi, ActionConsumable<string> openDetailPageFor, ItemStack stack, List<RichTextComponentBase> components, float marginBottom, ref bool __result)
    {
        GrindingHandbook.Reset();
        List<ItemStack> outputs = GrindingHandbook.Outputs(capi.World, stack);
        if (outputs.Count == 0)
        {
            return;
        }

        bool haveText = __result;
        CollectibleBehaviorHandbookTextAndExtraInfo.AddHeading(components, capi, "Grinds into", ref haveText);
        foreach (ItemStack output in outputs)
        {
            components.Add(new ItemstackTextComponent(capi, output, 40, 10, EnumFloat.Inline, GrindingHandbook.OpenPage(capi, openDetailPageFor))
            {
                ShowStacksize = true,
                PaddingLeft = 2
            });
        }

        components.Add(new ClearFloatTextComponent(capi, marginBottom));
        __result = haveText;
    }

    public static void Finalizer()
    {
        GrindingHandbook.Reset();
    }
}

// The quern's page: what it grinds lists every allowed result of every item.
[HarmonyPatchCategory(QuernChoices.HandbookPatchCategory)]
[HarmonyPatch(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), nameof(CollectibleBehaviorHandbookTextAndExtraInfo.addProcessorForInfo))]
public static class HandbookQuernPagePatch
{
    public static void Prefix(ItemStack stack)
    {
        if (stack.Block is BlockQuern)
        {
            GrindingHandbook.Current = GrindingHandbook.Mode.Hide;
        }
    }

    public static void Postfix(ICoreClientAPI capi, ItemStack[] allStacks, ActionConsumable<string> openDetailPageFor, ItemStack stack, List<RichTextComponentBase> components, ref bool __result)
    {
        GrindingHandbook.Reset();
        if (stack.Block is not BlockQuern)
        {
            return;
        }

        var outputs = new List<ItemStack>();
        foreach (ItemStack input in allStacks)
        {
            foreach (ItemStack output in GrindingHandbook.Outputs(capi.World, input))
            {
                if (!outputs.Any(known => known.Equals(capi.World, output, GlobalConstants.IgnoredStackAttributes)))
                {
                    outputs.Add(output);
                }
            }
        }

        if (outputs.Count == 0)
        {
            return;
        }

        // Same layout as the vanilla section this replaces.
        bool haveText = __result;
        CollectibleBehaviorHandbookTextAndExtraInfo.AddHeading(components, capi, "handbook-processorfor-grinding-title", ref haveText);
        int padding = 2;
        while (outputs.Count > 0)
        {
            ItemStack first = outputs[0];
            outputs.RemoveAt(0);
            components.Add(new SlideshowItemstackTextComponent(capi, first, outputs, 40, EnumFloat.Inline, GrindingHandbook.OpenPage(capi, openDetailPageFor))
            {
                PaddingLeft = padding
            });
            padding = 0;
        }

        components.Add(new RichTextComponent(capi, "\n", CairoFont.WhiteSmallText()));
        __result = haveText;
    }

    public static void Finalizer()
    {
        GrindingHandbook.Reset();
    }
}

// A result's page: "Created by" grinding lists every item that can grind into it.
[HarmonyPatchCategory(QuernChoices.HandbookPatchCategory)]
[HarmonyPatch(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "addCreatedByInfo")]
public static class HandbookCreatedByPatch
{
    public static void Prefix(ItemStack stack)
    {
        GrindingHandbook.Current = GrindingHandbook.Mode.MatchPage;
        GrindingHandbook.Page = stack;
    }

    public static void Finalizer()
    {
        GrindingHandbook.Reset();
    }
}
