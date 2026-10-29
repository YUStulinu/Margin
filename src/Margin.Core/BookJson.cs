using System.Text.Json;
using System.Text.Json.Serialization;

namespace Margin.Core;

/// <summary>A book as the companion service receives it: blocks and chapters with their positions.
/// The app parses the EPUB; the service never does, so both always agree on positions.</summary>
public sealed record BookJson(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("blocks")] IReadOnlyList<BookJson.BlockJson> Blocks,
    [property: JsonPropertyName("chapters")] IReadOnlyList<BookJson.ChapterJson> Chapters)
{
    public sealed record BlockJson(
        [property: JsonPropertyName("start")] int Start,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("heading")] bool Heading,
        [property: JsonPropertyName("chapter")] int Chapter);

    public sealed record ChapterJson(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("start")] int Start,
        [property: JsonPropertyName("end")] int End);

    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static BookJson From(Book b) => new(
        b.Id, b.Title, b.Author,
        b.Blocks.Select(x => new BlockJson(x.Start, x.Text, x.Kind == BlockKind.Heading, x.Chapter)).ToList(),
        b.Chapters.Select(c => new ChapterJson(c.Index, c.Title, c.Start, c.End)).ToList());
}
