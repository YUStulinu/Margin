"""Checking an answer for spoilers before the reader sees it.

Keeping later text out of the prompt is not enough: a language model may know the book. Ask it who
the murderer in The Mysterious Affair at Styles is and it can answer from memory. So every answer is
checked against the part of the book the reader has not read yet (the one place the service looks
at it, and only to compare):

- names: a name in the answer that the reader has not met yet but that the rest of the book uses
  is someone the reader has not been introduced to;
- closeness: each sentence of the answer is compared (embeddings) with every sentence read and every
  sentence not read yet. If it is much closer to one ahead than to any read, it is probably telling
  what happens there.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np

from .book import Horizon, sentences
from .index import Index, embed
from .names import names


@dataclass
class Verdict:
    spoiler: bool
    unseen_names: list[str] = field(default_factory=list)
    # (sentence, best read score, best unread score)
    close_to_unread: list[tuple[str, float, float]] = field(default_factory=list)


# A sentence is "about" a passage ahead when it scores this much more against it than against any read one.
MARGIN = 0.06
FLOOR = 0.72


def check(answer: str, h: Horizon, index: Index, margin: float = MARGIN, floor: float = FLOOR) -> Verdict:
    read_text = h.text().lower()
    unread = h.book._unread_text_for_checking(h.position).lower()
    unseen = []
    for n, _ in names(answer):
        key = n.lower()
        # Compare the name's own words, titles aside ("Mr. Hastings" -> "hastings").
        core = " ".join(w for w in key.replace(".", "").split() if w not in {"mr", "mrs", "miss", "dr", "sir", "lady", "lord"})
        if core and core not in read_text and core in unread and n not in unseen:
            unseen.append(n)
    close = []
    sents = [s for s in sentences(answer) if len(s.split()) >= 5]
    if sents:
        q = embed(sents)
        spans = np.array(index.sentences)
        read = np.where(spans[:, 1] <= h.position)[0]
        ahead = np.where(spans[:, 0] >= h.position)[0]
        if len(ahead):
            r = (q @ index.sentence_vectors[read].T).max(axis=1) if len(read) else np.zeros(len(sents))
            a = (q @ index.sentence_vectors[ahead].T).max(axis=1)
            for s, rs, as_ in zip(sents, r, a):
                if as_ >= floor and as_ - rs >= margin:
                    close.append((s, float(rs), float(as_)))
    return Verdict(spoiler=bool(unseen or close), unseen_names=unseen, close_to_unread=close)
