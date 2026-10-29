"""A small book with a secret, and a fake language model that records every prompt it is given."""

from __future__ import annotations

from pathlib import Path

import pytest

from margin_service.book import Block, Book, Chapter

ROOT = Path(__file__).resolve().parents[2]
BOOKS = ROOT / "data" / "json"

CHAPTERS = [
    ("Chapter 1. The Lighthouse", [
        "Mara Quill came to the island of Corran on the last ferry of the autumn.",
        "The lighthouse keeper, old Tobias Venn, met her at the quay with a lantern and a scowl.",
        "He told her that the light had failed twice that month, and that nobody in the village would say why.",
    ]),
    ("Chapter 2. The Village", [
        "In the village Mara met the schoolteacher, Edith Marsh, who laughed too loudly at everything.",
        "Edith said that Tobias Venn had once been a sailor and had lost a ship on the rocks below the light.",
        "The fisherman Hal Brennock warned Mara to keep away from the north cliff after dark.",
    ]),
    ("Chapter 3. The Storm", [
        "A storm came in from the west, and in the morning Tobias Venn was found dead at the foot of the stairs.",
        "The constable said it was an accident, but Mara saw that the lamp room had been locked from outside.",
        "She found a brass button in the oil store, the kind worn on a naval coat.",
    ]),
    ("Chapter 4. The Truth", [
        "The button belonged to Ambrose Kell, the harbourmaster, who had been Venn's first mate on the lost ship.",
        "Ambrose Kell confessed that he had pushed Venn down the stairs to keep the wreck's insurance fraud a secret.",
        "Edith Marsh had known all along and had kept silent because Kell was her brother.",
    ]),
]

SECRET_NAME = "Ambrose Kell"


def make_book() -> Book:
    blocks, chapters, pos = [], [], 0
    texts = []
    for ci, (title, paras) in enumerate(CHAPTERS):
        start = pos
        for j, t in enumerate([title] + paras):
            blocks.append(Block(pos, t, j == 0, ci))
            texts.append(t)
            pos += len(t) + 1
        chapters.append((ci, title, start))
    length = pos - 1
    ch = [Chapter(i, t, s, chapters[i + 1][2] if i + 1 < len(chapters) else length) for i, t, s in chapters]
    return Book("fixture-lighthouse", "The Lighthouse at Corran", "A Test", blocks, ch)


@pytest.fixture
def book() -> Book:
    return make_book()


class FakeLlm:
    """Answers with a canned reply and keeps every system message and prompt."""

    def __init__(self, reply: str = "The book has not told us yet."):
        self.reply = reply
        self.prompts: list[str] = []

    def chat(self, system: str, user: str, max_tokens: int = 300, temperature: float = 0.0) -> str:
        self.prompts.append(system + "\n" + user)
        return self.reply if not callable(self.reply) else self.reply(user)

    def ready(self) -> bool:
        return True


@pytest.fixture
def fake_llm() -> FakeLlm:
    return FakeLlm()


def real_books() -> list[Book]:
    return [Book.load(p) for p in sorted(BOOKS.glob("*.json"))]
