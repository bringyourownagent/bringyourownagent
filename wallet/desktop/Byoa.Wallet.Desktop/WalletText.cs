using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The text of the wallet's windows: the visitor selects it with the mouse (double click a word, triple click a line) and copies it
/// (Ctrl+C or the right button), for a model's name, a host, a code, a path. Inside a button, a tick box or a list row it stays plain text:
/// the press must still reach that control. It looks like a TextBlock (it takes that one's styles).
/// </summary>
sealed class WalletText : SelectableTextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    public WalletText()
    {
        SelectionBrush = new SolidColorBrush(Color.FromArgb(120, 232, 85, 46));
    }

    /// <summary>Whether the text sits in something that is pressed: then it is not selected.</summary>
    bool InControl =>
        this.FindAncestorOfType<Button>() is not null || this.FindAncestorOfType<ListBoxItem>() is not null
        || this.FindAncestorOfType<MenuItem>() is not null || this.FindAncestorOfType<ComboBox>() is not null || this.FindAncestorOfType<TabItem>() is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Cursor = InControl ? null : new Cursor(StandardCursorType.Ibeam);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!InControl) base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!InControl) base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!InControl) base.OnPointerReleased(e);
    }
}
