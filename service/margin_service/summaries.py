"""Chapter summaries, for chapters the reader has finished. A chapter is summarized from its own
text only (read through the Horizon), so a summary can never say what comes after it; long chapters
are summarized in parts and the parts combined."""

from __future__ import annotations

import json
from pathlib import Path

from .book import Chapter, Horizon, Spoiler
from .llm import Llm

PART_WORDS = 1800

SYSTEM = (
    "You summarize one chapter of a novel for a reader who has just read it. "
    "Use only the text given. Do not guess what happens next. Three to five sentences, past tense, no lists."
)


def summarize_chapter(llm: Llm, h: Horizon, chapter: Chapter) -> str:
    if chapter.end > h.position:
        raise Spoiler(f"chapter {chapter.index} is not finished")
    text = h.text(chapter.start, chapter.end)
    words = text.split()
    if len(words) < 40:
        return text.strip()
    if len(words) <= PART_WORDS:
        return llm.chat(SYSTEM, f"Chapter: {chapter.title}\n\n{text}", max_tokens=220)
    parts = [" ".join(words[i : i + PART_WORDS]) for i in range(0, len(words), PART_WORDS)]
    notes = [llm.chat("Summarize this part of a chapter in two or three sentences. Use only the text given.", p, max_tokens=150) for p in parts]
    return llm.chat(SYSTEM, f"Chapter: {chapter.title}\n\nSummaries of its parts, in order:\n" + "\n".join(notes), max_tokens=220)


class Summaries:
    """Summaries kept on disk per book, made when first asked for."""

    def __init__(self, llm: Llm, cache: Path):
        self.llm = llm
        self.cache = cache
        cache.mkdir(parents=True, exist_ok=True)

    def _file(self, book_id: str) -> Path:
        return self.cache / f"{book_id}.summaries.json"

    def known(self, book_id: str) -> dict[int, str]:
        f = self._file(book_id)
        return {int(k): v for k, v in json.loads(f.read_text(encoding="utf-8")).items()} if f.exists() else {}

    def get(self, h: Horizon, chapter: Chapter) -> str:
        known = self.known(h.book.id)
        if chapter.index not in known:
            known[chapter.index] = summarize_chapter(self.llm, h, chapter)
            self._file(h.book.id).write_text(json.dumps(known, indent=1), encoding="utf-8")
        return known[chapter.index]
