"""The retrieval index: the book cut into passages of about 120 words, each with its positions and
an embedding (bge-small-en-v1.5). A search sees only passages that end before the horizon; the one
the horizon falls in is cut there and embedded on the spot. Nothing past the horizon can be found
because nothing past it is ever scored.
"""

from __future__ import annotations

import threading
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .book import Book, Horizon

EMBEDDER = "BAAI/bge-small-en-v1.5"
EMBEDDER_REVISION = "5c38ec7c405ec4b44b94cc5a9bb96e735b38267a"
QUERY_PREFIX = "Represent this sentence for searching relevant passages: "
WORDS = 120


@dataclass(frozen=True)
class Passage:
    start: int
    end: int
    chapter: int


def passages(book: Book, words: int = WORDS) -> list[Passage]:
    """Consecutive blocks gathered up to `words` words; a long block is cut between sentences. A
    passage never crosses a chapter."""
    out: list[Passage] = []
    cur_start, cur_end, cur_words, cur_ch = None, None, 0, None
    for b in book.blocks:
        n = len(b.text.split())
        if cur_start is not None and (b.chapter != cur_ch or cur_words + n > words):
            out.append(Passage(cur_start, cur_end, cur_ch))
            cur_start = None
        if n > words:
            # Cut the block itself at sentence ends.
            pos, acc, acc_start = b.start, 0, b.start
            for s in _split_keep(b.text):
                k = len(s.split())
                if acc and acc + k > words:
                    out.append(Passage(acc_start, pos, b.chapter))
                    acc, acc_start = 0, pos
                acc += k
                pos += len(s)
            if pos > acc_start:
                out.append(Passage(acc_start, b.end, b.chapter))
            continue
        if cur_start is None:
            cur_start, cur_words, cur_ch = b.start, 0, b.chapter
        cur_end = b.end
        cur_words += n
    if cur_start is not None:
        out.append(Passage(cur_start, cur_end, cur_ch))
    return out


def _split_keep(text: str) -> list[str]:
    """Sentences with the space after them, so their lengths add up to the text's."""
    import re

    parts = re.split(r"(?<=[.!?])(\s+)", text)
    out, i = [], 0
    while i < len(parts):
        s = parts[i] + (parts[i + 1] if i + 1 < len(parts) else "")
        out.append(s)
        i += 2
    return out


_model = None
_lock = threading.Lock()


def embedder():
    global _model
    with _lock:
        if _model is None:
            import torch
            from sentence_transformers import SentenceTransformer

            _model = SentenceTransformer(EMBEDDER, revision=EMBEDDER_REVISION, device="cuda" if torch.cuda.is_available() else "cpu")
        return _model


def embed(texts: list[str], query: bool = False) -> np.ndarray:
    if not texts:
        return np.zeros((0, 384), np.float32)
    t = [QUERY_PREFIX + x for x in texts] if query else texts
    return embedder().encode(t, batch_size=64, normalize_embeddings=True, convert_to_numpy=True).astype(np.float32)


def sentence_spans(book: Book) -> list[tuple[int, int]]:
    """Every sentence of the book, as positions."""
    out = []
    for b in book.blocks:
        pos = b.start
        for s in _split_keep(b.text):
            t = s.rstrip()
            if len(t.split()) >= 3:
                out.append((pos, pos + len(t)))
            pos += len(s)
    return out


class Index:
    def __init__(self, book: Book, cache: Path | None = None):
        self.book = book
        self.passages = passages(book)
        self.sentences = sentence_spans(book)
        # Embedding the whole book here is fine: passages are only ever searched below the horizon,
        # and sentences ahead of it are only compared with answers (guard.py), never shown.
        self.vectors = self._vectors(cache, "passages", [book._text[p.start : p.end] for p in self.passages])
        self.sentence_vectors = self._vectors(cache, "sentences", [book._text[a:b] for a, b in self.sentences])
        self._starts = np.array([p.start for p in self.passages])
        self._ends = np.array([p.end for p in self.passages])

    def _vectors(self, cache: Path | None, kind: str, texts: list[str]) -> np.ndarray:
        f = cache / f"{self.book.id}.{kind}.npy" if cache else None
        if f is not None and f.exists():
            v = np.load(f)
            if len(v) == len(texts):
                return v
        v = embed(texts)
        if f is not None:
            f.parent.mkdir(parents=True, exist_ok=True)
            np.save(f, v)
        return v

    def search(self, h: Horizon, query: str, k: int = 6, exclude: tuple[int, int] | None = None) -> list[tuple[Passage, str, float]]:
        """The `k` read passages closest to `query`: (passage, its text, score), best first.
        `exclude` leaves out a range already in the prompt (the reader's selection)."""
        assert h.book is self.book
        q = embed([query], query=True)[0]
        read = np.where(self._ends <= h.position)[0]
        scores = self.vectors[read] @ q if len(read) else np.zeros(0)
        cands = [(self.passages[i], s) for i, s in zip(read, scores)]
        # The passage the horizon falls in, cut at the horizon.
        partial = np.where((self._starts < h.position) & (self._ends > h.position))[0]
        for i in partial:
            p = self.passages[i]
            cut = Passage(p.start, h.position, p.chapter)
            text = h.text(cut.start, cut.end)
            if len(text.split()) >= 8:
                cands.append((cut, float(embed([text])[0] @ q)))
        if exclude is not None:
            a, b = exclude
            cands = [(p, s) for p, s in cands if p.end <= a or p.start >= b]
        cands.sort(key=lambda x: -x[1])
        return [(p, h.text(p.start, p.end), float(s)) for p, s in cands[:k]]
