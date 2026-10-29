-- Margin's library: what you read, how far, what you noted and what you are learning.
-- Positions are character offsets into a book's text (see Book.cs).

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS books (
    id         TEXT PRIMARY KEY,        -- the hash of the book's text
    title      TEXT NOT NULL,
    author     TEXT NOT NULL,
    path       TEXT NOT NULL,
    length     INTEGER NOT NULL,
    added_at   TEXT NOT NULL,
    opened_at  TEXT
);

-- Where the reader is, and the furthest they have read: the spoiler horizon. The companion never
-- sees text past `horizon`; going back to reread does not lower it.
CREATE TABLE IF NOT EXISTS progress (
    book_id    TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE,
    position   INTEGER NOT NULL,
    horizon    INTEGER NOT NULL CHECK (horizon >= 0),
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS notes (
    id         INTEGER PRIMARY KEY,
    book_id    TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
    start      INTEGER NOT NULL,
    "end"      INTEGER NOT NULL CHECK ("end" >= start),
    quote      TEXT NOT NULL,
    note       TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS notes_by_book ON notes(book_id, start);

-- Vocabulary cards, scheduled with SM-2. `context` is the sentence the word was met in.
CREATE TABLE IF NOT EXISTS flashcards (
    id            INTEGER PRIMARY KEY,
    book_id       TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
    word          TEXT NOT NULL,
    meaning       TEXT NOT NULL,
    context       TEXT NOT NULL,
    position      INTEGER NOT NULL,
    due           TEXT NOT NULL,
    interval_days REAL NOT NULL DEFAULT 0,
    ease          REAL NOT NULL DEFAULT 2.5,
    reps          INTEGER NOT NULL DEFAULT 0,
    lapses        INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT NOT NULL,
    UNIQUE (book_id, word)
);
CREATE INDEX IF NOT EXISTS cards_due ON flashcards(due);

CREATE TABLE IF NOT EXISTS reviews (
    id            INTEGER PRIMARY KEY,
    card_id       INTEGER NOT NULL REFERENCES flashcards(id) ON DELETE CASCADE,
    grade         INTEGER NOT NULL CHECK (grade BETWEEN 0 AND 5),
    interval_days REAL NOT NULL,
    reviewed_at   TEXT NOT NULL
);

-- What was asked of the companion, at which horizon, and what it answered (or held back).
CREATE TABLE IF NOT EXISTS questions (
    id         INTEGER PRIMARY KEY,
    book_id    TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
    horizon    INTEGER NOT NULL,
    selection  TEXT NOT NULL DEFAULT '',
    question   TEXT NOT NULL,
    answer     TEXT NOT NULL,
    withheld   INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);
