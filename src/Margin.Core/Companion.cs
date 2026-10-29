using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Margin.Core;

public sealed record Answer(
    [property: JsonPropertyName("answer")] string Text,
    [property: JsonPropertyName("withheld")] bool Withheld,
    [property: JsonPropertyName("sources")] IReadOnlyList<Span> Sources);

public sealed record Span([property: JsonPropertyName("start")] int Start, [property: JsonPropertyName("end")] int End);

public sealed record CharacterInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("mentions")] int Mentions,
    [property: JsonPropertyName("first")] int First,
    [property: JsonPropertyName("chapter")] int Chapter,
    [property: JsonPropertyName("together")] IReadOnlyList<JsonElement> Together)
{
    /// <summary>(other character, paragraphs they share).</summary>
    public IEnumerable<(string Name, int Count)> Links => Together.Select(t => (t[0].GetString() ?? "", t[1].GetInt32()));
}

public sealed record WordInfo(
    [property: JsonPropertyName("word")] string Word,
    [property: JsonPropertyName("zipf")] double Zipf,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("context")] string Context);

/// <summary>
/// The companion service (service/, Python) over HTTP on this computer. Every call that touches the
/// book's text sends the reader's horizon: the service has no other idea of how far they have read.
/// </summary>
public sealed class Companion(HttpClient http)
{
    public Companion(string url = "http://127.0.0.1:8765") : this(new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(3) }) { }

    readonly HashSet<string> sent = [];

    public async Task<bool> Alive(CancellationToken ct = default)
    {
        try
        {
            using var r = await http.GetAsync("/health", ct);
            return r.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Gives the service the book's text with its positions (once per book).</summary>
    public async Task Send(Book book, CancellationToken ct = default)
    {
        if (sent.Contains(book.Id)) return;
        using var r = await http.PostAsJsonAsync("/books", BookJson.From(book), ct);
        r.EnsureSuccessStatusCode();
        sent.Add(book.Id);
    }

    public async Task<Answer> Ask(Book book, int horizon, string question, Span? selection, CancellationToken ct = default)
    {
        await Send(book, ct);
        using var r = await http.PostAsJsonAsync($"/books/{book.Id}/ask", new { horizon, question, selection = selection is null ? null : new { start = selection.Start, end = selection.End } }, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<Answer>(ct))!;
    }

    /// <summary>The summary of a finished chapter; null if the chapter is not finished yet.</summary>
    public async Task<string?> Summary(Book book, int horizon, int chapter, CancellationToken ct = default)
    {
        await Send(book, ct);
        using var r = await http.GetAsync($"/books/{book.Id}/chapters/{chapter}/summary?horizon={horizon}", ct);
        if (r.StatusCode == System.Net.HttpStatusCode.Forbidden) return null;
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("summary").GetString();
    }

    public async Task<List<CharacterInfo>> Characters(Book book, int horizon, CancellationToken ct = default)
    {
        await Send(book, ct);
        var j = await http.GetFromJsonAsync<JsonElement>($"/books/{book.Id}/characters?horizon={horizon}", ct);
        return j.GetProperty("characters").Deserialize<List<CharacterInfo>>()!;
    }

    public async Task<List<WordInfo>> Vocabulary(Book book, int horizon, int? chapter = null, CancellationToken ct = default)
    {
        await Send(book, ct);
        string q = chapter is null ? "" : $"&chapter={chapter}";
        var j = await http.GetFromJsonAsync<JsonElement>($"/books/{book.Id}/vocabulary?horizon={horizon}{q}", ct);
        return j.GetProperty("words").Deserialize<List<WordInfo>>()!;
    }

    public async Task<string> Meaning(string word, string context, CancellationToken ct = default)
    {
        using var r = await http.PostAsJsonAsync("/meaning", new { word, context }, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("meaning").GetString() ?? "";
    }
}
