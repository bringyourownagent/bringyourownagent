using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Byoa.Wallet.Android;

/// <summary>
/// A screen that is a column of things on one scrolling page (My models, Sites, Limits, Activity, Help). It is drawn when it is first shown and drawn
/// again whenever something it shows has changed, or when the visitor has changed what it shows; the page keeps its place while it is drawn again.
/// </summary>
abstract class ListPage : Page
{
    readonly ScrollViewer _scroll;

    protected ListPage(Shell shell) : base(shell)
    {
        Body = new StackPanel { Spacing = 12, Margin = new Thickness(16, 12, 16, 24) };
        _scroll = new ScrollViewer { Content = Body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>What the page puts its things into.</summary>
    protected StackPanel Body { get; }

    protected sealed override Control Build()
    {
        Draw();
        return _scroll;
    }

    public override void Refresh()
    {
        if (Built) Draw();
    }

    /// <summary>Draws the page again now (the visitor pressed something that changes what it shows).</summary>
    protected void Redraw()
    {
        if (Built) Draw();
    }

    /// <summary>Back to the top of the page (another page of a long list starts at its top).</summary>
    protected void ScrollToTop() => _scroll.Offset = new Vector(0, 0);

    void Draw()
    {
        Body.Children.Clear();
        Fill();
    }

    /// <summary>Puts the page's things into <see cref="Body"/>.</summary>
    protected abstract void Fill();
}
