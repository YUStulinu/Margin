namespace Margin.Core;

/// <summary>One laid-out line: the text between two positions, on one row of a page.</summary>
public sealed record Line(int Start, int End, BlockKind Kind, bool FirstOfBlock);

/// <summary>A page: the positions it shows, and its lines in order.</summary>
public sealed record Page(int Index, int Start, int End, IReadOnlyList<Line> Lines);

/// <summary>How text measures in the reader's font. The app gives the real widths; tests give fixed ones.</summary>
public interface ITextMeasure
{
    /// <summary>The width of a word (no spaces) in a block of this kind.</summary>
    double Width(string word, BlockKind kind);
    double SpaceWidth(BlockKind kind);
    double LineHeight(BlockKind kind);
    /// <summary>Extra space before a block (not at the top of a page).</summary>
    double BlockGap(BlockKind kind);
}

/// <summary>
/// Breaks a book into pages: words wrap greedily into lines of the page's width, lines fill pages of
/// its height. A heading never ends a page; a paragraph may continue on the next one. The app draws
/// exactly these lines, so what was measured is what is shown.
/// </summary>
public static class Paginator
{
    public static List<Page> Paginate(Book book, ITextMeasure measure, double width, double height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "the page has no room");
        var lines = new List<(Line Line, double Height, double Gap)>();
        var widths = new Dictionary<(string, BlockKind), double>();
        double W(string w, BlockKind k)
        {
            if (!widths.TryGetValue((w, k), out var x)) widths[(w, k)] = x = measure.Width(w, k);
            return x;
        }
        foreach (var block in book.Blocks)
        {
            bool first = true;
            foreach (var (s, e) in Wrap(block.Text, block.Kind, width, W, measure.SpaceWidth(block.Kind)))
            {
                lines.Add((new Line(block.Start + s, block.Start + e, block.Kind, first), measure.LineHeight(block.Kind), first ? measure.BlockGap(block.Kind) : 0));
                first = false;
            }
        }
        var pages = new List<Page>();
        var current = new List<Line>();
        double used = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var (line, h, gap) = lines[i];
            double need = (current.Count == 0 ? 0 : gap) + h;
            // A heading at the bottom of a page goes to the next one, with what follows it.
            bool lonelyHeading = line.Kind == BlockKind.Heading && i + 1 < lines.Count && lines[i + 1].Line.Kind != BlockKind.Heading
                                 && used + need + lines[i + 1].Gap + lines[i + 1].Height > height;
            if (current.Count > 0 && (used + need > height || lonelyHeading))
            {
                pages.Add(Close(pages.Count, current));
                current = [];
                used = 0;
                need = h;
            }
            current.Add(line);
            used += need;
        }
        if (current.Count > 0) pages.Add(Close(pages.Count, current));
        return pages;
    }

    static Page Close(int index, List<Line> lines) => new(index, lines[0].Start, lines[^1].End, lines);

    /// <summary>Line ranges (offsets in the text) for one block.</summary>
    internal static IEnumerable<(int Start, int End)> Wrap(string text, BlockKind kind, double width, Func<string, BlockKind, double> w, double space)
    {
        int lineStart = 0, lineEnd = 0;
        double lineWidth = 0;
        int i = 0;
        while (i < text.Length)
        {
            int ws = i;
            while (i < text.Length && text[i] != ' ') i++;
            string word = text[ws..i];
            double ww = w(word, kind);
            double add = lineEnd == lineStart ? ww : space + ww;
            if (lineEnd > lineStart && lineWidth + add > width)
            {
                yield return (lineStart, lineEnd);
                lineStart = ws;
                lineEnd = ws;
                lineWidth = 0;
                add = ww;
            }
            if (ww > width)
            {
                // A word wider than the page: cut it where it must be cut.
                int cut = ws;
                double cw = 0;
                for (int c = ws; c < i; c++)
                {
                    double x = w(text[c].ToString(), kind);
                    if (cw + x > width && c > cut)
                    {
                        yield return (cut, c);
                        cut = c;
                        cw = 0;
                    }
                    cw += x;
                }
                lineStart = cut;
                lineEnd = i;
                lineWidth = cw;
            }
            else
            {
                lineEnd = i;
                lineWidth += add;
            }
            while (i < text.Length && text[i] == ' ') i++;
        }
        if (lineEnd > lineStart) yield return (lineStart, lineEnd);
    }

    /// <summary>The page showing a position.</summary>
    public static int PageAt(IReadOnlyList<Page> pages, int position)
    {
        int lo = 0, hi = pages.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (pages[mid].Start <= position) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }
}
