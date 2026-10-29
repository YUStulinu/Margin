"""A book as the app sends it (Margin.Core's BookJson), and the one way to read its text: through a
Horizon.

The app parses the EPUB and gives every block its position (a character offset into the book's
text). The service never parses books itself, so both sides always agree on positions.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Block:
    start: int
    text: str
    heading: bool
    chapter: int

    @property
    def end(self) -> int:
        return self.start + len(self.text)


@dataclass(frozen=True)
class Chapter:
    index: int
    title: str
    start: int
    end: int


class Spoiler(Exception):
    """Something asked for text past the reader's horizon."""


class Book:
    def __init__(self, id: str, title: str, author: str, blocks: list[Block], chapters: list[Chapter]):
        self.id, self.title, self.author = id, title, author
        self.blocks, self.chapters = blocks, chapters
        self._text = "\n".join(b.text for b in blocks)
        self.length = len(self._text)
        for b in blocks:  # the positions are the text's
            if self._text[b.start : b.end] != b.text:
                raise ValueError(f"block at {b.start} does not match the text")

    @staticmethod
    def from_json(d: dict) -> "Book":
        return Book(
            d["id"], d["title"], d["author"],
            [Block(b["start"], b["text"], b["heading"], b["chapter"]) for b in d["blocks"]],
            [Chapter(c["index"], c["title"], c["start"], c["end"]) for c in d["chapters"]],
        )

    @staticmethod
    def load(path: str | Path) -> "Book":
        return Book.from_json(json.loads(Path(path).read_text(encoding="utf-8")))

    def horizon(self, position: int) -> "Horizon":
        return Horizon(self, position)

    def chapter_at(self, position: int) -> Chapter:
        found = self.chapters[0]
        for c in self.chapters:
            if c.start <= position:
                found = c
        return found

    # The whole text, for one purpose only: checking answers for spoilers (guard.py). Nothing read
    # from here may be put in a prompt.
    def _unread_text_for_checking(self, horizon: int) -> str:
        return self._text[horizon:]


class Horizon:
    """The part of a book the reader has read: [0, position). Every piece of text that goes to the
    language model, the retrieval index's queries, summaries and the character map is read through
    one of these, and asking for anything past `position` raises Spoiler."""

    def __init__(self, book: Book, position: int):
        if not 0 <= position <= book.length:
            raise ValueError(f"position {position} is outside the book (0-{book.length})")
        self.book = book
        self.position = position

    def text(self, start: int = 0, end: int | None = None) -> str:
        end = self.position if end is None else end
        if end > self.position:
            raise Spoiler(f"text up to {end} asked for, read up to {self.position}")
        return self.book._text[max(0, start) : end]

    def clip(self, start: int, end: int) -> tuple[int, int]:
        """A range cut at the horizon."""
        return min(start, self.position), min(end, self.position)

    def blocks(self) -> list[Block]:
        """The blocks read, the last one cut at the horizon."""
        out = []
        for b in self.book.blocks:
            if b.start >= self.position:
                break
            out.append(b if b.end <= self.position else Block(b.start, b.text[: self.position - b.start], b.heading, b.chapter))
        return out

    def finished_chapters(self) -> list[Chapter]:
        return [c for c in self.book.chapters if c.end <= self.position]

    def __repr__(self) -> str:
        return f"Horizon({self.book.title!r}, {self.position})"


# A sentence ends at . ! or ?, perhaps followed by a closing quote or bracket, before a space and a
# capital (or an opening quote).
_SENTENCE = re.compile(r"(?:(?<=[.!?])|(?<=[.!?][\"'”’)]))\s+(?=[\"'“‘(]?[A-Z])")


def sentences(text: str) -> list[str]:
    return [s for s in _SENTENCE.split(text) if s.strip()]
