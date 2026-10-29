namespace Margin.Core;

/// <summary>
/// A book as Margin reads it: its text as one string, cut into blocks (headings and paragraphs), and
/// its chapters. A <em>position</em> is a character offset into <see cref="Text"/>. Everything that
/// must not run ahead of the reader (the companion's context, summaries, the character map) is
/// measured in positions.
/// </summary>
public sealed class Book
{
    public required string Title { get; init; }
    public required string Author { get; init; }
    /// <summary>The blocks' text joined with "\n".</summary>
    public required string Text { get; init; }
    public required IReadOnlyList<Block> Blocks { get; init; }
    public required IReadOnlyList<Chapter> Chapters { get; init; }
    /// <summary>A stable identifier: the SHA-256 of the text, first 16 hex digits.</summary>
    public required string Id { get; init; }

    public int Length => Text.Length;

    /// <summary>The chapter a position falls in.</summary>
    public Chapter ChapterAt(int position)
    {
        Chapter found = Chapters[0];
        foreach (var c in Chapters)
        {
            if (c.Start <= position) found = c;
            else break;
        }
        return found;
    }

    /// <summary>The index of the block a position falls in (the last block for the end).</summary>
    public int BlockAt(int position)
    {
        int lo = 0, hi = Blocks.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Blocks[mid].Start <= position) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }
}

public enum BlockKind { Heading, Paragraph }

/// <param name="Start">Position of the block's first character.</param>
/// <param name="Chapter">Index into <see cref="Book.Chapters"/>.</param>
public sealed record Block(BlockKind Kind, string Text, int Start, int Chapter)
{
    public int End => Start + Text.Length;
}

/// <param name="Start">Position where the chapter begins.</param>
/// <param name="End">Position where the next one begins (or the book's length).</param>
public sealed record Chapter(int Index, string Title, int Start, int End);
