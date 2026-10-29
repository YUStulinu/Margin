using Margin.Core;

namespace Margin.Tests;

public class EpubTests
{
    [Fact]
    public void ReadsBlocksChaptersAndMetadata()
    {
        var b = EpubReader.Read(TestBooks.Small());
        Assert.Equal("The Small Book", b.Title);
        Assert.Equal("A. Writer", b.Author);
        Assert.Equal(
            ["Chapter One", "It was a dark and stormy night.", "The rain fell.", "The wind howled.", "Chapter Two", "Morning came at last.", "Chapter Three", "And then it was over."],
            b.Blocks.Select(x => x.Text));
        Assert.Equal(["Chapter One", "Chapter Two", "Chapter Three"], b.Chapters.Select(c => c.Title));
        Assert.Equal(BlockKind.Heading, b.Blocks[0].Kind);
        Assert.Equal(BlockKind.Paragraph, b.Blocks[1].Kind);
    }

    [Fact]
    public void LeavesOutTheGutenbergLicence()
    {
        var b = EpubReader.Read(TestBooks.Small());
        Assert.DoesNotContain("Gutenberg", b.Text);
    }

    [Fact]
    public void PositionsAreOffsetsIntoTheText()
    {
        var b = EpubReader.Read(TestBooks.Small());
        foreach (var x in b.Blocks) Assert.Equal(x.Text, b.Text.Substring(x.Start, x.Text.Length));
        Assert.Equal(0, b.Chapters[0].Start);
        for (int i = 1; i < b.Chapters.Count; i++) Assert.Equal(b.Chapters[i - 1].End, b.Chapters[i].Start);
        Assert.Equal(b.Length, b.Chapters[^1].End);
        Assert.Equal(1, b.ChapterAt(b.Blocks[5].Start).Index);
        Assert.Equal(5, b.BlockAt(b.Blocks[5].Start + 3));
    }

    [Fact]
    public void FallsBackToTheNcx()
    {
        var b = EpubReader.Read(TestBooks.Small(ncxOnly: true));
        Assert.Equal(["Chapter One", "Chapter Two", "Chapter Three"], b.Chapters.Select(c => c.Title));
    }

    [Fact]
    public void TheIdIsTheTextsHash()
    {
        Assert.Equal(EpubReader.Read(TestBooks.Small()).Id, EpubReader.Read(TestBooks.Small(ncxOnly: true)).Id);
    }

    public static IEnumerable<object[]> Gutenberg => TestBooks.Gutenberg.Select(p => new object[] { Path.GetFileName(p) });

    [Theory]
    [MemberData(nameof(Gutenberg))]
    public void ReadsTheGutenbergBooks(string file)
    {
        var b = EpubReader.Read(Path.Combine(TestBooks.Root, "data", "books", file));
        Assert.True(b.Length > 200_000, $"{b.Length} characters");
        Assert.True(b.Chapters.Count >= 10, $"{b.Chapters.Count} chapters");
        Assert.DoesNotContain("PROJECT GUTENBERG LICENSE", b.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(b.Chapters, c => c.Title.Contains("Gutenberg", StringComparison.OrdinalIgnoreCase));
        foreach (var c in b.Chapters) Assert.True(c.End >= c.Start);
    }
}
