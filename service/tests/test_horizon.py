"""Nothing past the reader's horizon reaches the language model, the search, the summaries, the
character map or the vocabulary.

The strongest test (`test_no_prompt_holds_unread_text`) runs the whole companion, with a fake model
that records its prompts, over every book at many horizons, and looks in each prompt for any run of
8 words that occurs in the book only after the horizon.
"""

from __future__ import annotations

import random
import re

import pytest

from margin_service import characters, companion, vocabulary
from margin_service.book import Book, Spoiler
from margin_service.index import Index, passages
from margin_service.summaries import Summaries

from .conftest import SECRET_NAME, FakeLlm, real_books

SHINGLE = 8


def shingles(text: str) -> set[tuple[str, ...]]:
    w = re.findall(r"[a-z0-9']+", text.lower())
    return {tuple(w[i : i + SHINGLE]) for i in range(len(w) - SHINGLE + 1)}


def unread_only(book: Book, horizon: int) -> set[tuple[str, ...]]:
    """Runs of 8 words found after the horizon and nowhere before it."""
    return shingles(book._text[horizon:]) - shingles(book._text[:horizon])


def test_horizon_refuses_text_past_it(book):
    h = book.horizon(book.chapters[2].start)
    assert h.text().endswith("north cliff after dark.\n") or "north cliff" in h.text()
    with pytest.raises(Spoiler):
        h.text(0, h.position + 1)
    assert SECRET_NAME not in h.text()
    assert all(b.end <= h.position for b in h.blocks())


def test_horizon_bounds(book):
    with pytest.raises(ValueError):
        book.horizon(book.length + 1)
    with pytest.raises(ValueError):
        book.horizon(-1)
    assert book.horizon(book.length).text() == book._text


def test_passages_cover_the_book_without_crossing_chapters():
    for b in real_books():
        ps = passages(b)
        for p in ps:
            assert p.start < p.end <= b.length
            assert b.chapters[p.chapter].start <= p.start and p.end <= b.chapters[p.chapter].end
        # Every block's text is in some passage.
        covered = sum(p.end - p.start for p in ps)
        assert covered >= sum(len(x.text) for x in b.blocks) * 0.99


@pytest.fixture(scope="module")
def indexed():
    return [(b, Index(b)) for b in real_books()]


def test_search_only_finds_read_passages(indexed):
    rng = random.Random(1)
    for b, ix in indexed:
        for _ in range(10):
            h = b.horizon(rng.randrange(1000, b.length))
            for p, text, _ in ix.search(h, "who did it and why", k=10):
                assert p.end <= h.position
                assert text == b._text[p.start : p.end]


QUESTIONS = ["Who is this person?", "Who killed him?", "How does the story end?", "What happens next?", "What does this word mean?"]


def test_no_prompt_holds_unread_text(indexed):
    rng = random.Random(7)
    for b, ix in indexed:
        for _ in range(12):
            position = rng.randrange(2000, b.length - 2000)
            h = b.horizon(position)
            ahead = unread_only(b, position)
            llm = FakeLlm()
            sel_end = position - rng.randrange(0, 400)
            sel = (max(0, sel_end - 300), sel_end)
            summaries = {c.index: f"summary of {c.title}" for c in h.finished_chapters()}
            companion.ask(llm, h, ix, rng.choice(QUESTIONS), sel, summaries, guard=False)
            # Even a selection reaching past the horizon is cut at it.
            companion.ask(llm, h, ix, rng.choice(QUESTIONS), (position - 100, position + 5000), summaries, guard=False)
            for prompt in llm.prompts:
                leaked = shingles(prompt) & ahead
                assert not leaked, f"{b.title} at {position}: {sorted(leaked)[:3]}"


def test_summaries_only_for_finished_chapters(book, tmp_path):
    llm = FakeLlm("A summary.")
    s = Summaries(llm, tmp_path)
    h = book.horizon(book.chapters[3].start)
    for c in h.finished_chapters():
        s.get(h, c)
    with pytest.raises(Spoiler):
        s.get(h, book.chapters[3])
    assert all(SECRET_NAME not in p for p in llm.prompts)
    assert len(h.finished_chapters()) == 3


def test_character_map_grows_with_the_horizon(book):
    early = {c.name for c in characters.character_map(book.horizon(book.chapters[3].start), min_mentions=1)}
    late = {c.name for c in characters.character_map(book.horizon(book.length), min_mentions=1)}
    assert "Tobias Venn" in early and "Edith Marsh" in early
    assert SECRET_NAME not in early
    assert SECRET_NAME in late


def test_character_map_never_reaches_past_the_horizon():
    rng = random.Random(3)
    for b in real_books():
        for _ in range(4):
            h = b.horizon(rng.randrange(5000, b.length))
            for c in characters.character_map(h):
                assert c.first < h.position


def test_vocabulary_comes_from_read_text():
    rng = random.Random(5)
    for b in real_books():
        h = b.horizon(rng.randrange(20000, b.length))
        for w in vocabulary.rare_words(h, limit=40):
            assert w.position < h.position
            assert w.context in h.text() or w.context.strip() in h.text()
