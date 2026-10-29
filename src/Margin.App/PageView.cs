using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Margin.Core;

namespace Margin.App;

/// <summary>Measures text in the reader's font, for the paginator.</summary>
sealed class ReaderMeasure(double fontSize) : ITextMeasure
{
    public static readonly FontFamily Serif = new("Georgia, Cambria, Times New Roman, serif");
    public readonly double FontSize = fontSize;
    readonly Typeface body = new(Serif);
    readonly Typeface head = new(Serif, FontStyle.Normal, FontWeight.Bold);

    public double HeadingSize => FontSize * 1.3;
    public double Size(BlockKind k) => k == BlockKind.Heading ? HeadingSize : FontSize;
    Typeface Face(BlockKind k) => k == BlockKind.Heading ? head : body;

    public double Width(string word, BlockKind kind) =>
        new FormattedText(word, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face(kind), Size(kind), null).WidthIncludingTrailingWhitespace;

    public double SpaceWidth(BlockKind kind) => Width("a b", kind) - Width("ab", kind);
    public double LineHeight(BlockKind kind) => Math.Ceiling(Size(kind) * 1.55);
    public double BlockGap(BlockKind kind) => kind == BlockKind.Heading ? FontSize * 1.6 : FontSize * 0.7;
}

/// <summary>
/// Draws one page exactly as the paginator laid it out: each block's lines on the page become one
/// selectable text, with line breaks where the paginator put them. A selection is reported as
/// positions in the book.
/// </summary>
sealed class PageView : StackPanel
{
    public event Action<int, int, string>? Selected;
    readonly List<(SelectableTextBlock View, List<(int Offset, int Start, int End)> Lines)> parts = [];

    public void Show(Book book, Page page, ReaderMeasure m)
    {
        Children.Clear();
        parts.Clear();
        int i = 0;
        while (i < page.Lines.Count)
        {
            var first = page.Lines[i];
            var group = new List<Line> { first };
            int block = book.BlockAt(first.Start);
            while (i + 1 < page.Lines.Count && !page.Lines[i + 1].FirstOfBlock) group.Add(page.Lines[++i]);
            i++;
            var map = new List<(int, int, int)>();
            var text = new System.Text.StringBuilder();
            foreach (var l in group)
            {
                if (text.Length > 0) text.Append('\n');
                map.Add((text.Length, l.Start, l.End));
                text.Append(book.Text, l.Start, l.End - l.Start);
            }
            var view = new SelectableTextBlock
            {
                Text = text.ToString(),
                FontFamily = ReaderMeasure.Serif,
                FontSize = m.Size(first.Kind),
                FontWeight = first.Kind == BlockKind.Heading ? FontWeight.Bold : FontWeight.Normal,
                LineHeight = m.LineHeight(first.Kind),
                TextWrapping = TextWrapping.NoWrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xF2, 0xC1, 0x4E)),
                Margin = new Thickness(0, Children.Count == 0 || !first.FirstOfBlock ? 0 : m.BlockGap(first.Kind), 0, 0),
            };
            var entry = (view, map);
            view.PropertyChanged += (_, e) =>
            {
                if (e.Property == SelectableTextBlock.SelectionEndProperty || e.Property == SelectableTextBlock.SelectionStartProperty)
                    Report(entry.view, entry.map);
            };
            parts.Add(entry);
            Children.Add(view);
            _ = block;
        }
    }

    void Report(SelectableTextBlock v, List<(int Offset, int Start, int End)> map)
    {
        int a = Math.Min(v.SelectionStart, v.SelectionEnd), b = Math.Max(v.SelectionStart, v.SelectionEnd);
        if (b <= a) return;
        foreach (var (other, _) in parts)
            if (other != v) other.SelectionStart = other.SelectionEnd = 0;
        Selected?.Invoke(ToPosition(map, a), ToPosition(map, b), v.SelectedText);
    }

    /// <summary>An index into a block's page text -> a position in the book.</summary>
    static int ToPosition(List<(int Offset, int Start, int End)> map, int index)
    {
        var line = map.Last(l => l.Offset <= index);
        return Math.Min(line.Start + (index - line.Offset), line.End);
    }

    /// <summary>Selects the first occurrence of `text` on the page (for screenshots).</summary>
    public bool Select(string text)
    {
        foreach (var (v, _) in parts)
        {
            int at = (v.Text ?? "").IndexOf(text, StringComparison.Ordinal);
            if (at >= 0)
            {
                v.SelectionStart = at;
                v.SelectionEnd = at + text.Length;
                return true;
            }
        }
        return false;
    }
}
