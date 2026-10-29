"""Vocabulary: rare words in what the reader has read (or in one chapter of it), with the sentence
each was met in, and on request their meaning there. Rarity is the word's frequency in English at
large (wordfreq's Zipf scale: 3 is about once per million words); names and very short words are left
out."""

from __future__ import annotations

import re
from dataclasses import dataclass

from wordfreq import zipf_frequency

from .book import Horizon, sentences
from .llm import Llm

_WORD = re.compile(r"[A-Za-z][a-z]+(?:-[a-z]+)?")


@dataclass
class Word:
    word: str
    zipf: float
    count: int
    position: int  # where it was first met
    context: str  # the sentence it was first met in


def rare_words(h: Horizon, start: int = 0, end: int | None = None, limit: int = 30, below: float = 3.0) -> list[Word]:
    end = h.position if end is None else min(end, h.position)
    found: dict[str, Word] = {}
    for b in h.blocks():
        if b.end <= start or b.start >= end:
            continue
        pos = b.start
        for s in sentences(b.text):
            for m in _WORD.finditer(s):
                w = m.group()
                at = pos + m.start()
                if at < start or at >= end or len(w) < 5:
                    continue
                low = w.lower()
                if w[0].isupper() and m.start() > 0:
                    continue  # a name, most likely
                if low in found:
                    found[low].count += 1
                    continue
                z = zipf_frequency(low, "en")
                if 0 < z < below:
                    found[low] = Word(low, z, 1, at, s.strip())
            pos += len(s) + 1
    words = sorted(found.values(), key=lambda w: (w.zipf, -w.count))
    return words[:limit]


def meaning(llm: Llm, word: str, context: str) -> str:
    return llm.chat(
        "You explain words for a reader of a novel. Give the meaning of the word as it is used in the sentence, "
        "in one short line, without repeating the sentence.",
        f"Word: {word}\nSentence: {context}",
        max_tokens=60,
    )
