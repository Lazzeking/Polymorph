using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Polymorph;

public sealed class PolymorphChoiceDialog : GuiDialog
{
    private const double Padding = 6;
    private const int MaxColumns = 8;

    private Action<int>? _onSelected;
    private List<ItemStack?> _outputs = new();
    private ChoiceInventory? _inventory;
    private double _buttonX;
    private double _buttonY;
    private double _buttonWidth;
    private double _buttonHeight;
    private bool _below;
    private bool _standalone;
    private string? _caption;
    private List<string?>? _labels;
    private List<string?>? _notes;

    // A caption gets this width at least, and as many lines above the slots as its text wraps into.
    private const double CaptionWidth = 280;
    private string _signature = "";

    public PolymorphChoiceDialog(ICoreClientAPI capi) : base(capi)
    {
    }

    public override string ToggleKeyCombinationCode => null!;

    // Beside a window the panel is a HUD, and the window frees the mouse. On its own it is a dialog, because the game
    // only frees the mouse for open dialogs.
    public override EnumDialogType DialogType => _standalone ? EnumDialogType.Dialog : EnumDialogType.HUD;

    public override double DrawOrder => 0.6;

    public override bool CaptureAllInputs()
    {
        return IsOpened();
    }

    public object? Owner { get; private set; }

    // Below puts the panel under the button instead of beside it, for a button that sits between two slots.
    // A caption explains the choice when the panel opens on its own, away from the window it belongs to.
    // Labels go under each result, and notes are added to its tooltip, for example when a barrel makes it.
    public void Open(double renderX, double renderY, double outerWidth, double outerHeight, IReadOnlyList<ItemStack?> outputs, int active, Action<int> onSelected, object owner, bool below = false, bool standalone = false, string? caption = null, IReadOnlyList<string?>? labels = null, IReadOnlyList<string?>? notes = null)
    {
        _below = below;
        _standalone = standalone;
        _caption = caption;
        _labels = labels?.ToList();
        _notes = notes?.ToList();
        _outputs = new List<ItemStack?>(outputs);
        _onSelected = onSelected;
        Owner = owner;
        _buttonX = renderX;
        _buttonY = renderY;
        _buttonWidth = outerWidth;
        _buttonHeight = outerHeight;
        _signature = Signature(outputs, active) + "|" + string.Join("|", labels ?? Array.Empty<string?>());
        Compose(renderX, renderY, outerWidth, outerHeight, active);
        if (!IsOpened())
        {
            TryOpen();
        }
    }

    // Rebuilds the open panel in place when its results or the selected one changed.
    public void Refresh(IReadOnlyList<ItemStack?> outputs, int active, Action<int> onSelected, IReadOnlyList<string?>? labels = null, IReadOnlyList<string?>? notes = null)
    {
        _onSelected = onSelected;
        string signature = Signature(outputs, active) + "|" + string.Join("|", labels ?? Array.Empty<string?>());
        if (!IsOpened() || signature == _signature)
        {
            return;
        }

        _signature = signature;
        _outputs = new List<ItemStack?>(outputs);
        _labels = labels?.ToList();
        _notes = notes?.ToList();
        Compose(_buttonX, _buttonY, _buttonWidth, _buttonHeight, active);
    }

    private static string Signature(IReadOnlyList<ItemStack?> outputs, int active)
    {
        return active + "|" + string.Join("|", outputs.Select(stack => stack?.Collectible?.Code + " x" + stack?.StackSize));
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
        if (index < 0 || index >= _outputs.Count)
        {
            return false;
        }

        Action<int>? onSelected = _onSelected;
        TryClose();
        onSelected?.Invoke(index);
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
        _outputs.Clear();
        _labels = null;
        _notes = null;
        _onSelected = null;
        Owner = null;
    }

    private void Compose(double renderX, double renderY, double outerWidth, double outerHeight, int active)
    {
        int columns = Math.Min(MaxColumns, _outputs.Count);
        int rows = (_outputs.Count + columns - 1) / columns;
        _inventory = new ChoiceInventory(columns * rows, capi);
        for (int i = 0; i < _outputs.Count; i++)
        {
            _inventory[i].Itemstack = _outputs[i]?.Clone();
            ((ChoiceSlot)_inventory[i]).Note = _notes != null && i < _notes.Count ? _notes[i] : null;
        }

        float scale = RuntimeEnv.GUIScale;
        double buttonHeight = outerHeight / scale;
        double slotSize = buttonHeight / PolymorphChoiceButton.SizeRatio;
        double slotsX = renderX / scale + outerWidth / scale + 4;
        double slotsY = renderY / scale - (slotSize - buttonHeight) / 2;

        // With labels every result gets a cell as wide as its label, with the label under its slot.
        CairoFont labelFont = CairoFont.WhiteDetailText().WithOrientation(EnumTextOrientation.Center);
        double slotSizeUnscaled = GuiElementPassiveItemSlot.unscaledSlotSize;
        double cellWidth = 0;
        double labelHeight = 0;
        ElementBounds slots;
        if (_labels != null)
        {
            cellWidth = slotSizeUnscaled + 8;
            foreach (string? label in _labels)
            {
                if (label != null)
                {
                    cellWidth = Math.Max(cellWidth, labelFont.GetTextExtents(label).Width / scale + 8);
                }
            }

            labelHeight = capi.Gui.Text.GetLineHeight(labelFont) / scale;
            slots = ElementBounds.Fixed(0, 0, columns * cellWidth, rows * (slotSizeUnscaled + labelHeight + 8));
        }
        else
        {
            slots = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 0, columns, rows);
        }

        ElementBounds? captionBounds = null;
        ElementBounds content = slots;
        CairoFont captionFont = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center);
        if (_caption != null)
        {
            // The text is measured at GUI scale, so the width goes in scaled and the height comes back scaled.
            double width = Math.Max(CaptionWidth, slots.fixedWidth);
            double height = capi.Gui.Text.GetMultilineTextHeight(captionFont, _caption, GuiElement.scaled(width)) / scale + 2;
            captionBounds = ElementBounds.Fixed(0, 0, width, height);
            slots.fixedX = (width - slots.fixedWidth) / 2;
            slots.fixedY = height + 6;
            content = ElementBounds.Fixed(0, 0, width, slots.fixedY + slots.fixedHeight);
        }

        ElementBounds dialog = content.ForkBoundingParent(Padding, Padding, Padding, Padding);
        dialog.Alignment = EnumDialogArea.None;
        dialog.fixedX = slotsX - Padding;
        dialog.fixedY = slotsY - Padding;

        double screenW = capi.Render.FrameWidth / scale;
        double screenH = capi.Render.FrameHeight / scale;
        if (_below)
        {
            dialog.fixedX = (renderX + outerWidth / 2) / scale - dialog.fixedWidth / 2;
            dialog.fixedY = (renderY + outerHeight) / scale + 4;
            if (dialog.fixedX + dialog.fixedWidth > screenW - 8)
            {
                dialog.fixedX = screenW - 8 - dialog.fixedWidth;
            }
        }
        else if (dialog.fixedX + dialog.fixedWidth > screenW - 8)
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
        GuiComposer composer = capi.Gui.CreateCompo("polymorphchoices", dialog)
            .AddShadedDialogBG(ElementBounds.Fill, false, 2, 0.95f);
        if (_caption != null && captionBounds != null)
        {
            composer.AddStaticText(_caption, captionFont, captionBounds);
        }

        if (_labels == null)
        {
            SingleComposer = composer
                .AddItemSlotGrid(_inventory, _ => { }, columns, slots, "choices")
                .Compose();
            SingleComposer.GetSlotGrid("choices")?.HighlightSlot(Math.Max(0, active));
            return;
        }

        double rowHeight = slotSizeUnscaled + labelHeight + 8;
        for (int i = 0; i < _outputs.Count; i++)
        {
            double cellX = slots.fixedX + (i % columns) * cellWidth;
            double cellY = slots.fixedY + (i / columns) * rowHeight;
            ElementBounds slot = ElementStdBounds.SlotGrid(EnumDialogArea.None, cellX + (cellWidth - slotSizeUnscaled) / 2, cellY, 1, 1);
            composer.AddItemSlotGrid(_inventory, _ => { }, 1, new[] { i }, slot, "choice" + i);
            string? label = i < _labels.Count ? _labels[i] : null;
            if (label != null)
            {
                composer.AddStaticText(label, labelFont, ElementBounds.Fixed(cellX, cellY + slotSizeUnscaled + 4, cellWidth, labelHeight));
            }
        }

        SingleComposer = composer.Compose();
        if (active >= 0 && active < _outputs.Count)
        {
            SingleComposer.GetSlotGrid("choice" + active)?.HighlightSlot(active);
        }
    }
}

internal sealed class ChoiceInventory : InventoryGeneric
{
    public ChoiceInventory(int count, ICoreAPI api)
        : base(count, "polymorph-choices", api, (_, inventory) => new ChoiceSlot(inventory))
    {
    }

    public override object ActivateSlot(int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op)
    {
        return null!;
    }
}

// A result slot whose tooltip can say one more thing about the result, such as when a barrel makes it.
internal sealed class ChoiceSlot : ItemSlot
{
    public ChoiceSlot(InventoryBase inventory) : base(inventory)
    {
    }

    // A little narrower than the tooltip's text column, which is about 288 wide.
    private const double NoteWidth = 240;

    private static readonly char[] Breaks = { '@', '+', '/', ':', '-' };

    public string? Note { get; set; }

    public override string GetStackDescription(IClientWorldAccessor world, bool extendedDebugInfo)
    {
        string description = base.GetStackDescription(world, extendedDebugInfo);
        return Note == null ? description : Wrap(Note) + "\n" + description;
    }

    // The game wraps a tooltip only at spaces, and a word wider than the whole tooltip is cut with an empty line left
    // behind it. Deny lines have no spaces, so a long one is broken here instead: after an @, +, /, : or -, or anywhere
    // when there is none.
    private static string Wrap(string note)
    {
        CairoFont font = CairoFont.WhiteSmallText();
        return string.Join("\n", note.Split('\n').Select(line => string.Join(" ", line.Split(' ').Select(word => WrapWord(font, word)))));
    }

    private static string WrapWord(CairoFont font, string word)
    {
        var parts = new List<string>();
        while (Width(font, word) > NoteWidth)
        {
            int fits = word.Length - 1;
            while (fits > 1 && Width(font, word[..fits]) > NoteWidth)
            {
                fits--;
            }

            int lastBreak = word.LastIndexOfAny(Breaks, fits - 1);
            int cut = lastBreak > 0 ? lastBreak + 1 : fits;
            parts.Add(word[..cut]);
            word = word[cut..];
        }

        parts.Add(word);
        return string.Join("\n", parts);
    }

    private static double Width(CairoFont font, string text)
    {
        return font.GetTextExtents(text).Width / RuntimeEnv.GUIScale;
    }
}
