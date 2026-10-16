using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Polymorph;

[HarmonyPatch(typeof(GuiDialogInventory), "ComposeSurvivalInvDialog")]
public static class SurvivalInventoryPatch
{
    public static void PatchOverhaulLib(Harmony harmony, ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("overhaullib"))
        {
            return;
        }

        System.Type? type = AccessTools.TypeByName("CombatOverhaul.Integration.GuiDialogPatches");
        MethodInfo? method = type == null ? null : AccessTools.DeclaredMethod(type, "GuiDialogInventory_ComposeSurvivalInvDialog");
        if (method == null)
        {
            api.Logger.Warning("Polymorph found Overhaul Lib but could not add the choice button to its inventory window.");
            return;
        }

        try
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(SurvivalInventoryPatch), nameof(Transpiler)));
            api.Logger.Notification("Polymorph added the choice button to the Overhaul Lib inventory window.");
        }
        catch (System.Exception exception)
        {
            api.Logger.Warning("Polymorph could not add the choice button to the Overhaul Lib inventory window: " + exception.Message);
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo compose = AccessTools.Method(typeof(GuiComposer), "Compose", new[] { typeof(bool) });
        CodeInstruction[] codes = instructions.ToArray();
        bool injected = false;
        for (int i = 0; i < codes.Length; i++)
        {
            if (!injected && i + 1 < codes.Length && codes[i + 1].Calls(compose))
            {
                yield return CodeInstruction.LoadArgument(0);
                yield return CodeInstruction.Call(typeof(SurvivalInventoryPatch), nameof(AddChoiceButton));
                injected = true;
            }

            yield return codes[i];
        }

        if (!injected)
        {
            throw new System.InvalidOperationException("Polymorph could not find GuiComposer.Compose in the survival inventory dialog.");
        }
    }

    private static GuiComposer AddChoiceButton(GuiComposer composer, GuiDialogInventory dialog)
    {
        if (dialog.craftingInvAccess() is not InventoryCraftingGrid)
        {
            return composer;
        }

        GuiElementItemSlotGrid? output = composer.GetSlotGrid("outputslot");
        if (output == null || dialog.capiAccess() is not ICoreClientAPI capi)
        {
            return composer;
        }

        PolymorphMod? mod = capi.ModLoader.GetModSystem<PolymorphMod>();
        if (mod == null)
        {
            return composer;
        }

        ElementBounds slot = output.Bounds;
        double slotSize = slot.fixedHeight > 0 ? slot.fixedHeight : 32;
        double size = slotSize * PolymorphChoiceButton.SizeRatio;
        ElementBounds bounds = ElementBounds.FixedSize(size, size);
        bounds.Alignment = slot.Alignment;
        bounds.ParentBounds = slot.ParentBounds;
        bounds.fixedX = slot.fixedX + slot.fixedWidth + 4;
        bounds.fixedY = slot.fixedY + (slotSize - size) / 2;
        composer.AddInteractiveElement(new PolymorphChoiceButton(capi, bounds, mod), "polymorphChoices");
        return composer;
    }
}

[HarmonyPatch(typeof(GuiDialogInventory), "OnGuiClosed")]
public static class SurvivalInventoryClosePatch
{
    public static void Postfix(GuiDialogInventory __instance)
    {
        if (__instance.capiAccess() is ICoreClientAPI capi)
        {
            capi.ModLoader.GetModSystem<PolymorphMod>()?.CloseChoices();
        }
    }
}

internal static class DialogAccess
{
    public static IInventory? craftingInvAccess(this GuiDialogInventory dialog)
    {
        return AccessTools.Field(typeof(GuiDialogInventory), "craftingInv").GetValue(dialog) as IInventory;
    }

    public static ICoreClientAPI? capiAccess(this GuiDialogInventory dialog)
    {
        return AccessTools.Field(typeof(GuiDialog), "capi").GetValue(dialog) as ICoreClientAPI;
    }
}

public sealed class PolymorphChoiceButton : GuiElementTextButton
{
    public const double SizeRatio = 0.75;

    private readonly PolymorphMod _mod;

    public PolymorphChoiceButton(ICoreClientAPI capi, ElementBounds bounds, PolymorphMod mod)
        : base(capi, "", CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText(), () => true, bounds, EnumButtonStyle.Normal)
    {
        _mod = mod;
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!_mod.HasAlternatives)
        {
            return;
        }

        base.RenderInteractiveElements(deltaTime);
        if (_mod.Icon == null)
        {
            return;
        }

        double inset = Bounds.OuterWidth * 0.22;
        api.Render.Render2DTexture(
            _mod.Icon.TextureId,
            (float)(Bounds.renderX + inset),
            (float)(Bounds.renderY + inset),
            (float)(Bounds.OuterWidth - inset * 2),
            (float)(Bounds.OuterHeight - inset * 2),
            120,
            new Vec4f(1, 1, 1, 1));
    }

    public override bool IsPositionInside(int posX, int posY)
    {
        return _mod.HasAlternatives && base.IsPositionInside(posX, posY);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!_mod.HasAlternatives)
        {
            return;
        }

        base.OnMouseDownOnElement(api, args);
        args.Handled = true;
        _mod.ToggleChoices(Bounds.renderX, Bounds.renderY, Bounds.OuterWidth, Bounds.OuterHeight);
    }

    public override string? MouseOverCursor => _mod.HasAlternatives ? "link" : null;
}
