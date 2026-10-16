using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Polymorph;

public sealed class PolymorphChoiceDialog : GuiDialog
{
    private const double Padding = 6;
    private const int MaxColumns = 8;

    private readonly Action<string> _onSelected;
    private List<GridRecipe> _recipes = new();
    private ChoiceInventory? _inventory;
    private double _buttonX;
    private double _buttonY;
    private double _buttonWidth;
    private double _buttonHeight;

    public PolymorphChoiceDialog(ICoreClientAPI capi, Action<string> onSelected) : base(capi)
    {
        _onSelected = onSelected;
    }

    public override string ToggleKeyCombinationCode => null!;

    public override EnumDialogType DialogType => EnumDialogType.HUD;

    public override double DrawOrder => 0.6;

    public override bool CaptureAllInputs()
    {
        return IsOpened();
    }

    public void Open(double renderX, double renderY, double outerWidth, double outerHeight, IReadOnlyList<GridRecipe> recipes, string activeKey)
    {
        _recipes = new List<GridRecipe>(recipes);
        _buttonX = renderX;
        _buttonY = renderY;
        _buttonWidth = outerWidth;
        _buttonHeight = outerHeight;
        Compose(renderX, renderY, outerWidth, outerHeight, activeKey);
        if (!IsOpened())
        {
            TryOpen();
        }
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (args.Handled || !IsOpened() || SingleComposer == null)
        {
            return;
        }

        if (SingleComposer.Bounds.PointInside(args.X, args.Y))
        {
            base.OnMouseDown(args);
            return;
        }

        // The choice button sits outside this panel. Leave the click for it so it presses, clicks, and toggles closed.
        if (OverButton(args.X, args.Y))
        {
            return;
        }

        TryClose();
        args.Handled = true;
    }

    private bool OverButton(int x, int y)
    {
        return x >= _buttonX && x < _buttonX + _buttonWidth && y >= _buttonY && y < _buttonY + _buttonHeight;
    }

    public override bool OnMouseClickSlot(ItemSlot slot)
    {
        if (_inventory == null || slot.Inventory != _inventory)
        {
            return false;
        }

        int index = _inventory.GetSlotId(slot);
        if (index < 0 || index >= _recipes.Count)
        {
            return false;
        }

        string key = PolymorphMod.RecipeKey(_recipes[index]);
        TryClose();
        _onSelected(key);
        return true;
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (!IsOpened() || args.KeyCode != (int)GlKeys.Escape)
        {
            return;
        }

        TryClose();
        args.Handled = true;
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        GuiComposer? composer = SingleComposer;
        if (composer != null)
        {
            // The composer setter rejects null, and the click that closed us may still be inside this composer.
            Composers.Remove("single");
            capi.Event.RegisterCallback(_ => composer.Dispose(), 1);
        }

        _inventory = null;
        _recipes.Clear();
    }

    private void Compose(double renderX, double renderY, double outerWidth, double outerHeight, string activeKey)
    {
        int columns = Math.Min(MaxColumns, _recipes.Count);
        int rows = (_recipes.Count + columns - 1) / columns;
        _inventory = new ChoiceInventory(columns * rows, capi);
        for (int i = 0; i < _recipes.Count; i++)
        {
            ItemStack? stack = _recipes[i].Output?.ResolvedItemStack?.Clone();
            _inventory[i].Itemstack = stack;
        }

        float scale = RuntimeEnv.GUIScale;
        double buttonHeight = outerHeight / scale;
        double slotSize = buttonHeight / PolymorphChoiceButton.SizeRatio;
        double slotsX = renderX / scale + outerWidth / scale + 4;
        double slotsY = renderY / scale - (slotSize - buttonHeight) / 2;

        ElementBounds slots = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 0, columns, rows);
        ElementBounds dialog = slots.ForkBoundingParent(Padding, Padding, Padding, Padding);
        dialog.Alignment = EnumDialogArea.None;
        dialog.fixedX = slotsX - Padding;
        dialog.fixedY = slotsY - Padding;

        double screenW = capi.Render.FrameWidth / scale;
        double screenH = capi.Render.FrameHeight / scale;
        if (dialog.fixedX + dialog.fixedWidth > screenW - 8)
        {
            double outputLeft = renderX / scale - 4 - slotSize;
            dialog.fixedX = outputLeft - 4 - dialog.fixedWidth;
        }

        if (dialog.fixedX < 8)
        {
            dialog.fixedX = 8;
        }

        if (dialog.fixedY + dialog.fixedHeight > screenH - 8)
        {
            dialog.fixedY = Math.Max(8, screenH - 8 - dialog.fixedHeight);
        }

        if (dialog.fixedY < 8)
        {
            dialog.fixedY = 8;
        }

        SingleComposer?.Dispose();
        SingleComposer = capi.Gui.CreateCompo("polymorphchoices", dialog)
            .AddShadedDialogBG(ElementBounds.Fill, false, 2, 0.95f)
            .AddItemSlotGrid(_inventory, _ => { }, columns, slots, "choices")
            .Compose();

        int active = 0;
        for (int i = 0; i < _recipes.Count; i++)
        {
            if (PolymorphMod.RecipeKey(_recipes[i]) == activeKey)
            {
                active = i;
                break;
            }
        }

        SingleComposer.GetSlotGrid("choices")?.HighlightSlot(active);
    }
}

internal sealed class ChoiceInventory : InventoryGeneric
{
    public ChoiceInventory(int count, ICoreAPI api)
        : base(count, "polymorph-choices", api, (_, inventory) => new ItemSlot(inventory))
    {
    }

    public override object ActivateSlot(int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op)
    {
        return null!;
    }
}
