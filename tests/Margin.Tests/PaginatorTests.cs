using Margin.Core;

namespace Margin.Tests;

/// <summary>Every character is one unit wide; lines are 1 high, headings 2, blocks 0.5 apart.</summary>
sealed class FixedMeasure : ITextMeasure
{
    public double Width(string word, BlockKind kind) => word.Length;
    public double SpaceWidth(BlockKind kind) => 1;
    public double LineHeight(BlockKind kind) => kind == BlockKind.Heading ? 2 : 1;
    public double BlockGap(BlockKind kind) => 0.5;
}

public class PaginatorTests
{
    static readonly FixedMeasure M = new();

    [Fact]
    public void LinesFitTheWidthAndBreakBetweenWords()
    {
        var lines = Paginator.Wrap("the quick brown fox jumps over the lazy dog", BlockKind.Paragraph, 15, M.Width, 1).ToList();
        var text = "the quick brown fox jumps over the lazy dog";
        Assert.Equal(["the quick brown", "fox jumps over", "the lazy dog"], lines.Select(l => text[l.Start..l.End]));
    }

    [Fact]
    public void AWordWiderThanThePageIsCut()
    {
        var lines = Paginator.Wrap("abcdefghijkl xy", BlockKind.Paragraph, 5, M.Width, 1).ToList();
        // The end of the cut word shares its line with what follows, if it fits.
        Assert.Equal([(0, 5), (5, 10), (10, 15)], lines);
    }

    [Theory]
    [InlineData(20, 6)]
    [InlineData(40, 10)]
    [InlineData(70, 30)]
    public void PagesCoverTheBookInOrderAndFit(double width, double height)
    {
        var b = EpubReader.Read(TestBooks.Small());
        var pages = Paginator.Paginate(b, M, width, height);
        Assert.Equal(0, pages[0].Start);
        Assert.Equal(b.Length, pages[^1].End);
        var lines = pages.SelectMany(p => p.Lines).ToList();
        // Every non-space character is on exactly one line, in order.
        string shown = string.Concat(lines.Select(l => b.Text[l.Start..l.End].Replace(" ", "")));
        Assert.Equal(b.Text.Replace(" ", "").Replace("\n", ""), shown);
        foreach (var p in pages)
        {
            double h = 0;
            for (int i = 0; i < p.Lines.Count; i++)
            {
                var l = p.Lines[i];
                Assert.True(l.End - l.Start <= width || !b.Text[l.Start..l.End].Contains(' '));
                h += (i > 0 && l.FirstOfBlock ? 0.5 : 0) + M.LineHeight(l.Kind);
            }
            Assert.True(h <= height || p.Lines.Count == 1, $"page {p.Index} is {h} high");
            // A heading does not end a page when something follows it.
            if (p.Index < pages.Count - 1) Assert.NotEqual(BlockKind.Heading, p.Lines[^1].Kind);
        }
    }

    [Fact]
    public void FindsThePageOfAPosition()
    {
        var b = EpubReader.Read(Path.Combine(TestBooks.Root, "data", "books", "study-in-scarlet.epub"));
        var pages = Paginator.Paginate(b, M, 60, 30);
        Assert.True(pages.Count > 100);
        foreach (int pos in new[] { 0, 1000, b.Length / 2, b.Length - 1 })
        {
            var p = pages[Paginator.PageAt(pages, pos)];
            Assert.True(p.Start <= pos && (pos < p.End || p.Index == pages.Count - 1 || pos < pages[p.Index + 1].Start));
        }
    }
}
