"""The guard, names, the character map's merging, vocabulary and the HTTP service."""

from __future__ import annotations

from collections import Counter

from fastapi.testclient import TestClient

from margin_service import guard
from margin_service.characters import merge_forms
from margin_service.companion import WITHHELD, ask
from margin_service.index import Index
from margin_service.names import lowercase_words, names
from margin_service.server import create_app

from .conftest import SECRET_NAME, FakeLlm


def test_names():
    # With the book's own lower-case words, as the character map uses them: "perhaps" and "then" are
    # words here, "long" is not written in lower case, so "Long John Silver" is a name.
    text = "perhaps it rained, perhaps not. Perhaps Annie saw Mr. Darcy and Long John Silver. Then they left; then they ate."
    found = [n for n, _ in names(text, lowercase_words(text))]
    assert found == ["Annie", "Mr. Darcy", "Long John Silver"]
    assert [n for n, _ in names("CHAPTER IV. THE 16TH OF JULY")] == []


def test_merging_keeps_titles_apart():
    root = merge_forms(Counter({"Mrs. Inglethorp": 5, "Mr. Inglethorp": 3, "Alfred Inglethorp": 4, "Alfred": 2, "Inglethorp": 2, "Hastings": 3, "Mr. Hastings": 4}))
    assert root["Alfred"] == "Alfred Inglethorp"
    assert root["Hastings"] == "Mr. Hastings"
    assert root["Mrs. Inglethorp"] == "Mrs. Inglethorp" and root["Mr. Inglethorp"] == "Mr. Inglethorp"
    assert root["Inglethorp"] == "Inglethorp"  # ambiguous: left alone


def test_guard_flags_a_name_the_reader_has_not_met(book):
    ix = Index(book)
    h = book.horizon(book.chapters[3].start)
    v = guard.check(f"It was {SECRET_NAME}, the harbourmaster.", h, ix)
    assert v.spoiler and SECRET_NAME in v.unseen_names
    ok = guard.check("Tobias Venn was the lighthouse keeper who met Mara at the quay.", h, ix)
    assert not ok.unseen_names


def test_guard_flags_telling_what_happens_ahead(book):
    ix = Index(book)
    h = book.horizon(book.chapters[3].start)
    # No new name, but it tells the last chapter.
    v = guard.check("Edith Marsh had known all along and kept silent because the killer was her own brother.", h, ix)
    assert v.spoiler and v.close_to_unread


def test_a_spoiling_answer_is_asked_again_then_withheld(book):
    ix = Index(book)
    h = book.horizon(book.chapters[3].start)
    llm = FakeLlm(f"{SECRET_NAME} pushed him down the stairs.")
    a = ask(llm, h, ix, "Who killed Tobias Venn?")
    assert a.withheld and a.text == WITHHELD and len(llm.prompts) == 2
    llm = FakeLlm("The constable thinks it was an accident, but Mara found the lamp room locked from outside.")
    a = ask(llm, h, ix, "Who killed Tobias Venn?")
    assert not a.withheld and len(llm.prompts) == 1


def test_service(book, tmp_path):
    from margin_service.book import Book as _B  # noqa: F401

    client = TestClient(create_app(FakeLlm("An answer."), tmp_path))
    j = {
        "id": book.id, "title": book.title, "author": book.author,
        "blocks": [{"start": b.start, "text": b.text, "heading": b.heading, "chapter": b.chapter} for b in book.blocks],
        "chapters": [{"index": c.index, "title": c.title, "start": c.start, "end": c.end} for c in book.chapters],
    }
    assert client.post("/books", json=j).status_code == 200
    h = book.chapters[2].start
    r = client.post(f"/books/{book.id}/ask", json={"horizon": h, "question": "Who is Edith?", "selection": {"start": 400, "end": 450}})
    assert r.status_code == 200 and r.json()["answer"] == "An answer."
    assert all(s["end"] <= h for s in r.json()["sources"])
    assert client.get(f"/books/{book.id}/chapters/3/summary", params={"horizon": h}).status_code == 403
    assert client.get(f"/books/{book.id}/chapters/1/summary", params={"horizon": h}).status_code == 200
    chars = client.get(f"/books/{book.id}/characters", params={"horizon": book.length}).json()["characters"]
    assert any(c["name"] == "Tobias Venn" for c in chars)
    assert client.get("/books/nope/characters", params={"horizon": 0}).status_code == 404
