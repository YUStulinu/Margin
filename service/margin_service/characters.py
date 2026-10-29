"""The character map, built from the part read: who has appeared, how often, where first, and who
appears with whom (in the same paragraph). It grows as the reader reads; nothing in it comes from
past the horizon, because it is counted from the Horizon's blocks only.

Forms of one name are merged when that is safe:
- "Alfred" goes into "Alfred Inglethorp" if no other name contains "Alfred";
- "Hastings" goes into "Mr. Hastings" if that is the only person called Hastings;
- "Mrs. Inglethorp" and "Mr. Inglethorp" are never merged (a title is a different person).
A bare surname shared by several people ("Inglethorp") stays on its own.
"""

from __future__ import annotations

from collections import Counter, defaultdict
from dataclasses import dataclass, field

from .book import Horizon
from .names import lowercase_words, names, split_title


@dataclass
class Character:
    name: str
    aliases: list[str]
    mentions: int
    first: int  # position of the first mention
    chapter: int  # the chapter of the first mention
    together: list[tuple[str, int]] = field(default_factory=list)  # (other character, paragraphs shared)


def _contains(longer: str, shorter: str) -> bool:
    return f" {shorter} " in f" {longer} " and longer != shorter


def merge_forms(counts: Counter) -> dict[str, str]:
    """Each form -> the form it is merged into (itself if none)."""
    forms = list(counts)
    parsed = {f: split_title(f) for f in forms}
    target: dict[str, str] = {}
    for f in forms:
        title, bare = parsed[f]
        if title is None:
            # Untitled: into the one other form that names it: the same name with a title ("Hastings"
            # -> "Mr. Hastings") or a longer one ("Alfred" -> "Alfred Inglethorp").
            cands = [g for g in forms if g != f and (parsed[g][1] == bare or _contains(parsed[g][1], bare))]
        else:
            # Titled: only into a longer form with the same title.
            cands = [g for g in forms if g != f and parsed[g][0] == title and _contains(parsed[g][1], bare)]
        if len(cands) == 1:
            target[f] = cands[0]
    return {f: _root(target, f) for f in forms}


def _root(target: dict[str, str], f: str) -> str:
    seen = set()
    while f in target and f not in seen:
        seen.add(f)
        f = target[f]
    return f


PLACE_WORDS = {"at", "to", "from", "in", "near", "towards", "toward", "into", "through", "of"}


def character_map(h: Horizon, min_mentions: int = 3, top_links: int = 5) -> list[Character]:
    per_block: list[list[str]] = []
    counts: Counter = Counter()
    placed: Counter = Counter()  # times a name follows "at", "to", "from"...: a place, most likely
    first: dict[str, tuple[int, int]] = {}
    lower = lowercase_words(h.text())
    for b in h.blocks():
        if b.heading:
            continue  # chapter titles are not where people appear
        found = []
        for n, at in names(b.text, lower):
            counts[n] += 1
            before = b.text[max(0, at - 12) : at].split()
            if before and before[-1].lower() in PLACE_WORDS and split_title(n)[0] is None:
                placed[n] += 1
            first.setdefault(n, (b.start + at, b.chapter))
            found.append(n)
        per_block.append(found)
    for n in [n for n in counts if placed[n] * 2 > counts[n]]:
        del counts[n]
    per_block = [[n for n in ns if n in counts] for ns in per_block]
    root = merge_forms(counts)
    total: Counter = Counter()
    aliases: dict[str, set[str]] = defaultdict(set)
    firsts: dict[str, tuple[int, int]] = {}
    for n, c in counts.items():
        r = root[n]
        total[r] += c
        aliases[r].add(n)
        if r not in firsts or first[n][0] < firsts[r][0]:
            firsts[r] = first[n]
    keep = {r for r, c in total.items() if c >= min_mentions}
    pairs: Counter = Counter()
    for ns in per_block:
        present = sorted({root[n] for n in ns} & keep)
        for i in range(len(present)):
            for j in range(i + 1, len(present)):
                pairs[(present[i], present[j])] += 1
    links: dict[str, Counter] = defaultdict(Counter)
    for (a, b), c in pairs.items():
        links[a][b] = c
        links[b][a] = c
    out = [Character(r, sorted(aliases[r] - {r}), total[r], firsts[r][0], firsts[r][1], links[r].most_common(top_links)) for r in keep]
    out.sort(key=lambda c: -c.mentions)
    return out
