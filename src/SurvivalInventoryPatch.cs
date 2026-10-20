using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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
            api.Logger.Warning("Polymorph could not add the choice button to the Overhaul Lib inventory window: {0}", exception.Message);
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return BeforeCompose(instructions, typeof(SurvivalInventoryPatch), nameof(AddChoiceButton), "the survival inventory dialog");
    }

    // Calls hook(composer, this) on the composer just before its Compose(bool) call. The hook returns the composer.
    internal static IEnumerable<CodeInstruction> BeforeCompose(IEnumerable<CodeInstruction> instructions, System.Type hookType, string hookName, string where)
    {
        MethodInfo compose = AccessTools.Method(typeof(GuiComposer), "Compose", new[] { typeof(bool) });
        CodeInstruction[] codes = instructions.ToArray();
        bool injected = false;
        for (int i = 0; i < codes.Length; i++)
        {
            if (!injected && i + 1 < codes.Length && codes[i + 1].Calls(compose))
            {
                yield return CodeInstruction.LoadArgument(0);
                yield return CodeInstruction.Call(hookType, hookName);
                injected = true;
            }

            yield return codes[i];
        }

        if (!injected)
        {
            throw new System.InvalidOperationException("Polymorph could not find GuiComposer.Compose in " + where + ".");
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

        PolymorphChoiceButton.AddTo(composer, capi, PolymorphChoiceButton.BoundsBeside(output.Bounds), mod);
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

    public static ICoreClientAPI? capiAccess(this GuiDialog dialog)
    {
        return AccessTools.Field(typeof(GuiDialog), "capi").GetValue(dialog) as ICoreClientAPI;
    }
}

public interface IPolymorphChoices
{
    bool HasAlternatives { get; }

    // True once the choice can no longer change, such as a quern that has started grinding.
    bool Locked { get; }

    // What the button says on hover while it is locked.
    string LockedLangKey => "polymorph:result-locked";

    void ToggleChoices(double renderX, double renderY, double outerWidth, double outerHeight);
}

public sealed class PolymorphChoiceButton : GuiElementTextButton
{
    public const double SizeRatio = 0.75;

    private static readonly Vec4f Normal = new(1, 1, 1, 1);
    private static readonly Vec4f Dimmed = new(1, 1, 1, 0.35f);

    private readonly IPolymorphChoices _choices;
    private readonly PolymorphMod? _mod;
    private GuiElementHoverText? _hover;
    private bool? _hoverLocked;

    public PolymorphChoiceButton(ICoreClientAPI capi, ElementBounds bounds, IPolymorphChoices choices)
        : base(capi, "", CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText(), () => true, bounds, EnumButtonStyle.Normal)
    {
        _choices = choices;
        _mod = capi.ModLoader.GetModSystem<PolymorphMod>();
    }

    // Adds the button and its hover text, which only shows while the button does.
    public static void AddTo(GuiComposer composer, ICoreClientAPI capi, ElementBounds bounds, IPolymorphChoices choices)
    {
        var button = new PolymorphChoiceButton(capi, bounds, choices);
        composer.AddInteractiveElement(button, "polymorphChoices");
        composer.AddHoverText(Lang.Get("polymorph:change-result"), CairoFont.WhiteSmallText(), 200, bounds.FlatCopy(), "polymorphChoicesHover");
        button._hover = composer.GetHoverText("polymorphChoicesHover");
        button._hover?.SetAutoWidth(true);
    }

    // The progress arrow between an input and an output slot ends about 32 below the top of the slots.
    private const double BelowArrow = 34;

    // Shifts a button between two slots left of center, clear of the arrow head.
    private const double LeftOfCenter = 10;

    // Centered between the two slots, just under the progress arrow. It reaches into the window's bottom padding
    // without being a child of the padded panel, so the window keeps its vanilla size.
    public static ElementBounds BoundsBetween(ElementBounds input, ElementBounds output)
    {
        double size = (output.fixedHeight > 0 ? output.fixedHeight : 48) * SizeRatio;
        ElementBounds bounds = ElementBounds.FixedSize(size, size);
        bounds.Alignment = output.Alignment;
        bounds.ParentBounds = output.ParentBounds;
        bounds.fixedX = (input.fixedX + input.fixedWidth + output.fixedX - size) / 2 - LeftOfCenter;
        bounds.fixedY = output.fixedY + BelowArrow;
        return bounds;
    }

    public static ElementBounds BoundsBeside(ElementBounds slot)
    {
        double slotSize = slot.fixedHeight > 0 ? slot.fixedHeight : 32;
        double size = slotSize * SizeRatio;
        ElementBounds bounds = ElementBounds.FixedSize(size, size);
        bounds.Alignment = slot.Alignment;
        bounds.ParentBounds = slot.ParentBounds;
        bounds.fixedX = slot.fixedX + slot.fixedWidth + 4;
        bounds.fixedY = slot.fixedY + (slotSize - size) / 2;
        return bounds;
    }

    private bool Clickable => _choices.HasAlternatives && !_choices.Locked;

    public override void RenderInteractiveElements(float deltaTime)
    {
        bool visible = _choices.HasAlternatives;
        UpdateHover(visible);
        if (!visible)
        {
            return;
        }

        base.RenderInteractiveElements(deltaTime);
        LoadedTexture? icon = _mod?.Icon;
        if (icon == null)
        {
            return;
        }

        double inset = Bounds.OuterWidth * 0.22;
        api.Render.Render2DTexture(
            icon.TextureId,
            (float)(Bounds.renderX + inset),
            (float)(Bounds.renderY + inset),
            (float)(Bounds.OuterWidth - inset * 2),
            (float)(Bounds.OuterHeight - inset * 2),
            120,
            _choices.Locked ? Dimmed : Normal);
    }

    private void UpdateHover(bool visible)
    {
        if (_hover == null)
        {
            return;
        }

        _hover.SetAutoDisplay(visible);
        bool locked = visible && _choices.Locked;
        if (_hoverLocked != locked)
        {
            _hoverLocked = locked;
            _hover.SetNewText(Lang.Get(locked ? _choices.LockedLangKey : "polymorph:change-result"));
        }
    }

    public override bool IsPositionInside(int posX, int posY)
    {
        return Clickable && base.IsPositionInside(posX, posY);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!Clickable)
        {
            return;
        }

        base.OnMouseDownOnElement(api, args);
        args.Handled = true;
        _choices.ToggleChoices(Bounds.renderX, Bounds.renderY, Bounds.OuterWidth, Bounds.OuterHeight);
    }

    public override string? MouseOverCursor => Clickable ? "link" : null;
}
