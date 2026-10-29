using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Margin.Core.Storage;

public sealed record BookRow(string Id, string Title, string Author, string Path, int Length, int Position, int Horizon, DateTime? OpenedAt);
public sealed record Note(long Id, int Start, int End, string Quote, string Text, DateTime CreatedAt);
public sealed record Card(long Id, string BookId, string Word, string Meaning, string Context, int Position, DateTime Due, double IntervalDays, double Ease, int Reps, int Lapses);
public sealed record Question(long Id, int Horizon, string Selection, string Text, string Answer, bool Withheld, DateTime CreatedAt);

/// <summary>The SQLite library (schema.sql). One connection, used from the UI thread.</summary>
public sealed class Library : IDisposable
{
    readonly SqliteConnection db;
    readonly Func<DateTime> now;

    public Library(string path, Func<DateTime>? clock = null)
    {
        now = clock ?? (() => DateTime.UtcNow);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
        db.Open();
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Margin.Core.Storage.schema.sql")!;
        Exec(new StreamReader(s).ReadToEnd());
    }

    public void Dispose() => db.Dispose();

    static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    int Exec(string sql, params (string, object?)[] args)
    {
        using var c = Cmd(sql, args);
        return c.ExecuteNonQuery();
    }

    SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (n, v) in args) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return c;
    }

    // --- books and progress --------------------------------------------------------------------

    /// <summary>Adds a book (or updates its path) and gives it a progress row at the start.</summary>
    public void AddBook(Book b, string path)
    {
        string t = Iso(now());
        Exec("INSERT INTO books(id, title, author, path, length, added_at) VALUES($id, $t, $a, $p, $l, $now) " +
             "ON CONFLICT(id) DO UPDATE SET path = excluded.path",
             ("$id", b.Id), ("$t", b.Title), ("$a", b.Author), ("$p", path), ("$l", b.Length), ("$now", t));
        Exec("INSERT OR IGNORE INTO progress(book_id, position, horizon, updated_at) VALUES($id, 0, 0, $now)", ("$id", b.Id), ("$now", t));
    }

    public List<BookRow> Books()
    {
        using var c = Cmd("SELECT b.id, b.title, b.author, b.path, b.length, p.position, p.horizon, b.opened_at FROM books b " +
                          "JOIN progress p ON p.book_id = b.id ORDER BY COALESCE(b.opened_at, b.added_at) DESC");
        using var r = c.ExecuteReader();
        var list = new List<BookRow>();
        while (r.Read())
            list.Add(new BookRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6),
                r.IsDBNull(7) ? null : Parse(r.GetString(7))));
        return list;
    }

    public (int Position, int Horizon) Progress(string bookId)
    {
        using var c = Cmd("SELECT position, horizon FROM progress WHERE book_id = $id", ("$id", bookId));
        using var r = c.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
    }

    public void Opened(string bookId) => Exec("UPDATE books SET opened_at = $now WHERE id = $id", ("$id", bookId), ("$now", Iso(now())));

    /// <summary>The reader is at `position` and has read up to `readTo` (the end of the page they
    /// are on). The horizon only ever moves forward.</summary>
    public int Read(string bookId, int position, int readTo)
    {
        Exec("UPDATE progress SET position = $pos, horizon = MAX(horizon, $to), updated_at = $now WHERE book_id = $id",
             ("$id", bookId), ("$pos", position), ("$to", readTo), ("$now", Iso(now())));
        return Progress(bookId).Horizon;
    }

    /// <summary>Moves to a position without counting it as read (a look ahead in the table of
    /// contents). The horizon stays where it is.</summary>
    public void Jump(string bookId, int position) =>
        Exec("UPDATE progress SET position = $pos, updated_at = $now WHERE book_id = $id", ("$id", bookId), ("$pos", position), ("$now", Iso(now())));

    /// <summary>Starting over: the horizon goes back to where the reader is.</summary>
    public void ResetHorizon(string bookId, int to) =>
        Exec("UPDATE progress SET horizon = $to, position = MIN(position, $to), updated_at = $now WHERE book_id = $id",
             ("$id", bookId), ("$to", to), ("$now", Iso(now())));

    // --- notes ---------------------------------------------------------------------------------

    public long AddNote(string bookId, int start, int end, string quote, string note)
    {
        Exec("INSERT INTO notes(book_id, start, \"end\", quote, note, created_at) VALUES($b, $s, $e, $q, $n, $now)",
             ("$b", bookId), ("$s", start), ("$e", end), ("$q", quote), ("$n", note), ("$now", Iso(now())));
        return LastId();
    }

    public List<Note> Notes(string bookId)
    {
        using var c = Cmd("SELECT id, start, \"end\", quote, note, created_at FROM notes WHERE book_id = $b ORDER BY start", ("$b", bookId));
        using var r = c.ExecuteReader();
        var list = new List<Note>();
        while (r.Read()) list.Add(new Note(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetString(4), Parse(r.GetString(5))));
        return list;
    }

    public void DeleteNote(long id) => Exec("DELETE FROM notes WHERE id = $id", ("$id", id));

    // --- flashcards ----------------------------------------------------------------------------

    /// <summary>Adds a card, due now. A word already in this book's deck is left as it is.</summary>
    public bool AddCard(string bookId, string word, string meaning, string context, int position)
    {
        string t = Iso(now());
        return Exec("INSERT OR IGNORE INTO flashcards(book_id, word, meaning, context, position, due, created_at) VALUES($b, $w, $m, $c, $p, $now, $now)",
                    ("$b", bookId), ("$w", word.Trim().ToLowerInvariant()), ("$m", meaning), ("$c", context), ("$p", position), ("$now", t)) > 0;
    }

    public List<Card> DueCards(int limit = 50, string? bookId = null)
    {
        using var c = Cmd("SELECT id, book_id, word, meaning, context, position, due, interval_days, ease, reps, lapses FROM flashcards " +
                          "WHERE due <= $now AND ($b IS NULL OR book_id = $b) ORDER BY due LIMIT $n",
                          ("$now", Iso(now())), ("$b", bookId), ("$n", limit));
        return ReadCards(c);
    }

    public List<Card> Cards(string bookId)
    {
        using var c = Cmd("SELECT id, book_id, word, meaning, context, position, due, interval_days, ease, reps, lapses FROM flashcards WHERE book_id = $b ORDER BY position", ("$b", bookId));
        return ReadCards(c);
    }

    static List<Card> ReadCards(SqliteCommand c)
    {
        using var r = c.ExecuteReader();
        var list = new List<Card>();
        while (r.Read())
            list.Add(new Card(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), Parse(r.GetString(6)),
                r.GetDouble(7), r.GetDouble(8), r.GetInt32(9), r.GetInt32(10)));
        return list;
    }

    /// <summary>Records a review (grade 0-5) and schedules the card's next one.</summary>
    public Card Review(long cardId, int grade)
    {
        using var c = Cmd("SELECT id, book_id, word, meaning, context, position, due, interval_days, ease, reps, lapses FROM flashcards WHERE id = $id", ("$id", cardId));
        var card = ReadCards(c).Single();
        var next = Sm2.Next(new Sm2.State(card.IntervalDays, card.Ease, card.Reps), grade);
        var t = now();
        var due = t.AddDays(next.IntervalDays);
        Exec("UPDATE flashcards SET interval_days = $i, ease = $e, reps = $r, lapses = lapses + $l, due = $d WHERE id = $id",
             ("$id", cardId), ("$i", next.IntervalDays), ("$e", next.Ease), ("$r", next.Reps), ("$l", grade < 3 ? 1 : 0), ("$d", Iso(due)));
        Exec("INSERT INTO reviews(card_id, grade, interval_days, reviewed_at) VALUES($id, $g, $i, $now)",
             ("$id", cardId), ("$g", grade), ("$i", next.IntervalDays), ("$now", Iso(t)));
        return card with { IntervalDays = next.IntervalDays, Ease = next.Ease, Reps = next.Reps, Due = due, Lapses = card.Lapses + (grade < 3 ? 1 : 0) };
    }

    // --- the companion's history ---------------------------------------------------------------

    public long AddQuestion(string bookId, int horizon, string selection, string question, string answer, bool withheld)
    {
        Exec("INSERT INTO questions(book_id, horizon, selection, question, answer, withheld, created_at) VALUES($b, $h, $s, $q, $a, $w, $now)",
             ("$b", bookId), ("$h", horizon), ("$s", selection), ("$q", question), ("$a", answer), ("$w", withheld ? 1 : 0), ("$now", Iso(now())));
        return LastId();
    }

    public List<Question> Questions(string bookId)
    {
        using var c = Cmd("SELECT id, horizon, selection, question, answer, withheld, created_at FROM questions WHERE book_id = $b ORDER BY id", ("$b", bookId));
        using var r = c.ExecuteReader();
        var list = new List<Question>();
        while (r.Read()) list.Add(new Question(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5) != 0, Parse(r.GetString(6))));
        return list;
    }

    long LastId()
    {
        using var c = Cmd("SELECT last_insert_rowid()");
        return (long)c.ExecuteScalar()!;
    }
}
