using System.Net;
using System.Text;
using System.Text.Json;
using Margin.Core;
using Margin.Core.Storage;

namespace Margin.Tests;

public class LibraryTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"margin-test-{Guid.NewGuid():N}.db");
    DateTime clock = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    readonly Library lib;
    readonly Book book = EpubReader.Read(TestBooks.Small());

    public LibraryTests()
    {
        lib = new Library(path, () => clock);
        lib.AddBook(book, "small.epub");
    }

    public void Dispose()
    {
        lib.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public void TheHorizonOnlyMovesForward()
    {
        Assert.Equal(40, lib.Read(book.Id, 10, 40));
        Assert.Equal(90, lib.Read(book.Id, 60, 90));
        // Going back to reread: the position moves, the horizon stays.
        Assert.Equal(90, lib.Read(book.Id, 5, 30));
        Assert.Equal((5, 90), lib.Progress(book.Id));
        // A look at a later chapter in the table of contents does not count as reading it.
        lib.Jump(book.Id, 150);
        Assert.Equal((150, 90), lib.Progress(book.Id));
        lib.ResetHorizon(book.Id, 20);
        Assert.Equal((20, 20), lib.Progress(book.Id));
    }

    [Fact]
    public void AddingABookTwiceKeepsItsProgress()
    {
        lib.Read(book.Id, 50, 70);
        lib.AddBook(book, "moved.epub");
        Assert.Equal((50, 70), lib.Progress(book.Id));
        Assert.Equal("moved.epub", lib.Books().Single().Path);
    }

    [Fact]
    public void NotesAndQuestions()
    {
        long id = lib.AddNote(book.Id, 5, 12, "dark and", "nice");
        lib.AddNote(book.Id, 1, 3, "t w", "");
        Assert.Equal([1, 5], lib.Notes(book.Id).Select(n => n.Start));
        lib.DeleteNote(id);
        Assert.Single(lib.Notes(book.Id));
        lib.AddQuestion(book.Id, 40, "dark", "Who?", "Nobody yet.", false);
        Assert.Equal("Nobody yet.", lib.Questions(book.Id).Single().Answer);
    }

    [Fact]
    public void CardsAreScheduledWithSm2()
    {
        Assert.True(lib.AddCard(book.Id, "Stormy", "with storms", "a dark and stormy night", 10));
        Assert.False(lib.AddCard(book.Id, "stormy", "again", "", 10));  // one card per word and book
        var card = lib.DueCards().Single();
        Assert.Equal("stormy", card.Word);
        card = lib.Review(card.Id, 5);
        Assert.Equal(1, card.IntervalDays);
        Assert.Empty(lib.DueCards());
        clock = clock.AddDays(1);
        card = lib.Review(lib.DueCards().Single().Id, 4);
        Assert.Equal(6, card.IntervalDays);
        clock = clock.AddDays(6);
        card = lib.Review(lib.DueCards().Single().Id, 4);
        Assert.Equal(Math.Round(6 * 2.6, 1), card.IntervalDays);
        clock = clock.AddDays(card.IntervalDays);
        card = lib.Review(lib.DueCards().Single().Id, 1);  // forgotten: tomorrow again
        Assert.Equal(1, card.IntervalDays);
        Assert.Equal(1, card.Lapses);
    }

    [Fact]
    public void Sm2Ease()
    {
        var s = new Sm2.State(0, 2.5, 0);
        s = Sm2.Next(s, 3);
        Assert.Equal(2.36, s.Ease, 2);
        for (int i = 0; i < 20; i++) s = Sm2.Next(s, 3);
        Assert.True(s.Ease >= 1.3);
        Assert.Throws<ArgumentOutOfRangeException>(() => Sm2.Next(s, 6));
    }
}

public class CompanionTests
{
    sealed class Recorder : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            string body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Requests.Add((r.Method.Method, r.RequestUri!.PathAndQuery, body));
            string json = r.RequestUri.AbsolutePath switch
            {
                "/books" => """{"id":"x","passages":3}""",
                var p when p.EndsWith("/ask") => """{"answer":"Not yet.","withheld":false,"sources":[{"start":0,"end":10}]}""",
                var p when p.Contains("/summary") => """{"summary":"It rained."}""",
                _ => "{}",
            };
            var code = r.RequestUri.AbsolutePath.Contains("/chapters/2/") ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task SendsTheBookOnceAndTheHorizonEveryTime()
    {
        var rec = new Recorder();
        var c = new Companion(new HttpClient(rec) { BaseAddress = new Uri("http://test") });
        var book = EpubReader.Read(TestBooks.Small());
        var a = await c.Ask(book, 42, "Who?", new Span(3, 9));
        Assert.Equal("Not yet.", a.Text);
        await c.Ask(book, 50, "Why?", null);
        Assert.Single(rec.Requests, x => x.Path == "/books");
        var asks = rec.Requests.Where(x => x.Path.EndsWith("/ask")).Select(x => JsonDocument.Parse(x.Body).RootElement).ToList();
        Assert.Equal([42, 50], asks.Select(x => x.GetProperty("horizon").GetInt32()));
        Assert.Equal(3, asks[0].GetProperty("selection").GetProperty("start").GetInt32());
        // The book went with its positions.
        var sent = JsonDocument.Parse(rec.Requests.First(x => x.Path == "/books").Body).RootElement;
        Assert.Equal(book.Blocks.Count, sent.GetProperty("blocks").GetArrayLength());
        Assert.Equal("It rained.", await c.Summary(book, 60, 1));
        Assert.Null(await c.Summary(book, 60, 2));  // not finished: the service says 403
    }
}
