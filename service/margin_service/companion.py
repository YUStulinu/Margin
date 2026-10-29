"""Answering a question about a passage, from the part of the book the reader has read.

The prompt holds:
- what the reader selected;
- the text just before it;
- the passages read that are closest to the question;
- what earlier chapters were about.

All of it comes through the Horizon. The answer is then checked (guard.py). If it looks like a
spoiler, the model is asked once more, more strictly. If the second answer still looks like one,
it is held back.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from .book import Horizon
from .guard import Verdict, check
from .index import Index
from .llm import Llm

SYSTEM = (
    "You are Margin, a reading companion. The reader is partway through \"{title}\" by {author}. "
    "You are shown excerpts from the part they have read, and nothing else. "
    "Answer only from those excerpts. Never say or hint at what happens later in the book, even if you know the book: "
    "the reader has not got there, and telling them would spoil it. "
    "If the excerpts do not answer the question, say the book has not told us yet. "
    "Answer in one to four plain sentences."
)

STRICT = (
    " Your earlier answer seemed to go beyond the excerpts. Use only what the excerpts say; "
    "do not name anyone or anything they do not mention."
)

WITHHELD = "I'd rather not answer that yet: what I could say might give away what is still ahead of you."


@dataclass
class Answer:
    text: str
    withheld: bool = False
    sources: list[tuple[int, int]] = field(default_factory=list)
    verdicts: list[Verdict] = field(default_factory=list)
    prompt: str = ""


def build_prompt(h: Horizon, index: Index, question: str, selection: tuple[int, int] | None, summaries: dict[int, str], k: int = 6) -> tuple[str, list[tuple[int, int]]]:
    parts = []
    sources = []
    if selection is not None:
        s, e = h.clip(*selection)
        before_start = max(0, s - 1500)
        parts.append(f"Just before the passage:\n{h.text(before_start, s)}")
        parts.append(f"The passage the reader selected:\n«{h.text(s, e)}»")
        sources.append((s, e))
        exclude = (before_start, e)
    else:
        exclude = None
    # Passages for the question itself, and for the question about the selection ("who is he?"): a
    # selection the question is not about should not crowd out what it is about.
    found = index.search(h, question, k=k, exclude=exclude)
    if selection is not None:
        seen = {p.start for p, _, _ in found}
        about = index.search(h, question + " " + h.text(*h.clip(*selection)), k=k // 2, exclude=exclude)
        found = found[: k - k // 2] + [x for x in about if x[0].start not in seen]
        found = found[:k]
    found.sort(key=lambda x: x[0].start)  # in reading order
    if found:
        lines = []
        for p, text, _ in found:
            title = h.book.chapters[p.chapter].title
            lines.append(f"[{title}] {text}")
            sources.append((p.start, p.end))
        parts.insert(0, "Excerpts from the part read so far:\n" + "\n\n".join(lines))
    done = [c for c in h.finished_chapters() if c.index in summaries]
    if done:
        parts.insert(0, "What the chapters read so far were about:\n" + "\n".join(f"- {c.title}: {summaries[c.index]}" for c in done[-8:]))
    parts.append(f"Question: {question}")
    return "\n\n".join(parts), sources


def ask(llm: Llm, h: Horizon, index: Index, question: str, selection: tuple[int, int] | None = None, summaries: dict[int, str] | None = None,
        guard: bool = True) -> Answer:
    system = SYSTEM.format(title=h.book.title, author=h.book.author)
    prompt, sources = build_prompt(h, index, question, selection, summaries or {})
    text = llm.chat(system, prompt)
    if not guard:
        return Answer(text, sources=sources, prompt=prompt)
    v = check(text, h, index)
    verdicts = [v]
    if v.spoiler:
        text = llm.chat(system + STRICT, prompt)
        v = check(text, h, index)
        verdicts.append(v)
        if v.spoiler:
            return Answer(WITHHELD, withheld=True, sources=sources, verdicts=verdicts, prompt=prompt)
    return Answer(text, sources=sources, verdicts=verdicts, prompt=prompt)
