"""Proper names in text: runs of capitalized words, with a title in front when there is one ("Mr.
Darcy", "Dr. Bauerstein", "Long John Silver"). Common English words at the front of a run are
dropped ("Perhaps Annie" -> "Annie"); words in capitals (headings) are not names."""

from __future__ import annotations

import re
from collections import Counter
from functools import lru_cache

from wordfreq import zipf_frequency

TITLES = {"mr", "mrs", "miss", "ms", "dr", "sir", "lady", "lord", "captain", "colonel", "major", "inspector",
          "sergeant", "doctor", "professor", "mademoiselle", "monsieur", "madame", "squire", "aunt", "uncle"}
# Capitalized but not people's names.
NOT_NAMES = {"i", "god", "heaven", "chapter", "christmas", "english", "french", "monday", "tuesday", "wednesday", "thursday",
             "friday", "saturday", "sunday", "january", "february", "march", "april", "may", "june", "july", "august",
             "september", "october", "november", "december", "oh", "ah", "alas", "hullo", "hallo", "hush", "aye", "ay", "o"}

_TOKEN = re.compile(r"[A-Za-z][A-Za-z'’\-]*\.?|[^\sA-Za-z]")


@lru_cache(maxsize=50000)
def common(word: str) -> bool:
    """A frequent English word: "Then", "Perhaps", "Was" are not names."""
    w = re.sub(r"['’](s|t|ll|re|ve|d|m)$", "", word.lower())
    return zipf_frequency(w, "en") >= 4.3 or w in NOT_NAMES


def _capitalized(t: str) -> bool:
    return t[0].isupper() and not (len(t.rstrip(".")) > 1 and t.rstrip(".").isupper())


def lowercase_words(text: str) -> Counter:
    """How often each word is written in lower case: a book's own sense of what is a word."""
    return Counter(m.group() for m in re.finditer(r"\b[a-z][a-z'’]*\b", text))


def names(text: str, lowercase: Counter | None = None) -> list[tuple[str, int]]:
    """(name, offset) for each name in `text`, in order. A title is kept with the name after it.
    `lowercase` (from lowercase_words over a whole book) tells ordinary words from names better than
    English at large: "Elizabeth" is a common word in general but never written "elizabeth"."""

    def ordinary(w: str) -> bool:
        w = re.sub(r"['’](s|t|ll|re|ve|d|m)$", "", w)
        if w.lower() in NOT_NAMES:
            return True
        if lowercase is not None:
            return lowercase[w.lower()] >= 1
        return common(w)

    out = []
    toks = [(m.group(), m.start()) for m in _TOKEN.finditer(text)]
    i = 0
    while i < len(toks):
        t, at = toks[i]
        if not (t[0].isalpha() and _capitalized(t)):
            i += 1
            continue
        j = i
        run: list[tuple[str, int]] = []
        while j < len(toks) and toks[j][0][0].isalpha() and _capitalized(toks[j][0]):
            w = toks[j][0]
            run.append((w, toks[j][1]))
            j += 1
            if w.endswith(".") and w.rstrip(".").lower() not in TITLES:
                break  # a sentence ends inside the run
        # Leading words that are common English (and not titles) are not part of the name.
        while run and run[0][0].rstrip(".").lower() not in TITLES and ordinary(run[0][0].rstrip(".")):
            run.pop(0)
        # Trailing titles name nobody.
        while run and run[-1][0].rstrip(".").lower() in TITLES:
            run.pop()
        if run:
            words = [w if w.rstrip(".").lower() in TITLES else w.rstrip(".") for w, _ in run]
            name = re.sub(r"['’]s$", "", " ".join(words))
            # A single word that is also an ordinary word ("Will", "Rose") counts only with a title.
            if len(name.replace(".", "")) > 1 and not (len(run) == 1 and ordinary(name)):
                out.append((name, run[0][1]))
        i = max(j, i + 1)
    return out


def split_title(name: str) -> tuple[str | None, str]:
    """("Mr.", "Darcy") from "Mr. Darcy"; (None, "Elizabeth Bennet") from "Elizabeth Bennet"."""
    ws = name.split()
    if ws and ws[0].rstrip(".").lower() in TITLES and len(ws) > 1:
        return ws[0].rstrip(".").lower(), " ".join(ws[1:])
    return None, name


def name_counts(text: str) -> Counter:
    return Counter(n for n, _ in names(text))
