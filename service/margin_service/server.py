"""The companion service the reader app talks to (HTTP, JSON, on 127.0.0.1 only).

    python -m margin_service.server [--model models/Qwen2.5-7B-Instruct-Q4_K_M.gguf] [--port 8765]

Every request that touches a book's text carries the reader's horizon; the service holds no other
idea of how far they have read.
"""

from __future__ import annotations

import argparse
import threading
from dataclasses import asdict
from pathlib import Path

import uvicorn
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

from . import characters, companion, vocabulary
from .book import Book, Spoiler
from .index import Index
from .llm import Llm, default_model, serve
from .summaries import Summaries

ROOT = Path(__file__).resolve().parents[2]
CACHE = ROOT / "data" / "service"


class Selection(BaseModel):
    start: int
    end: int


class AskRequest(BaseModel):
    horizon: int
    question: str
    selection: Selection | None = None


class MeaningRequest(BaseModel):
    word: str
    context: str


def create_app(llm: Llm, cache: Path = CACHE) -> FastAPI:
    app = FastAPI(title="Margin companion")
    books: dict[str, Book] = {}
    indexes: dict[str, Index] = {}
    summaries = Summaries(llm, cache)
    lock = threading.Lock()

    def get(book_id: str, horizon: int) -> tuple[Book, Index, "object"]:
        if book_id not in books:
            raise HTTPException(404, "unknown book: send it to POST /books first")
        b = books[book_id]
        if not 0 <= horizon <= b.length:
            raise HTTPException(400, f"horizon {horizon} outside the book")
        return b, indexes[book_id], b.horizon(horizon)

    @app.get("/health")
    def health():
        return {"ok": True, "llm": llm.ready(), "books": len(books)}

    @app.post("/books")
    def add_book(book: dict):
        b = Book.from_json(book)
        with lock:
            if b.id not in books:
                indexes[b.id] = Index(b, cache)
                books[b.id] = b
        return {"id": b.id, "passages": len(indexes[b.id].passages)}

    @app.post("/books/{book_id}/ask")
    def ask(book_id: str, req: AskRequest):
        b, ix, h = get(book_id, req.horizon)
        sel = (req.selection.start, req.selection.end) if req.selection else None
        known = summaries.known(b.id)
        with lock:
            a = companion.ask(llm, h, ix, req.question, sel, known)
        return {"answer": a.text, "withheld": a.withheld, "sources": [{"start": s, "end": e} for s, e in a.sources]}

    @app.get("/books/{book_id}/chapters/{index}/summary")
    def summary(book_id: str, index: int, horizon: int):
        b, _, h = get(book_id, horizon)
        if not 0 <= index < len(b.chapters):
            raise HTTPException(404, "no such chapter")
        try:
            with lock:
                return {"chapter": index, "title": b.chapters[index].title, "summary": summaries.get(h, b.chapters[index])}
        except Spoiler:
            raise HTTPException(403, "that chapter is not finished yet")

    @app.get("/books/{book_id}/characters")
    def chars(book_id: str, horizon: int):
        _, _, h = get(book_id, horizon)
        return {"characters": [asdict(c) for c in characters.character_map(h)]}

    @app.get("/books/{book_id}/vocabulary")
    def vocab(book_id: str, horizon: int, chapter: int | None = None, limit: int = 30):
        b, _, h = get(book_id, horizon)
        start, end = (b.chapters[chapter].start, b.chapters[chapter].end) if chapter is not None else (0, None)
        return {"words": [asdict(w) for w in vocabulary.rare_words(h, start, end, limit)]}

    @app.post("/meaning")
    def mean(req: MeaningRequest):
        with lock:
            return {"word": req.word, "meaning": vocabulary.meaning(llm, req.word, req.context)}

    return app


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default=str(default_model()))
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--llm-port", type=int, default=8090)
    ap.add_argument("--llm-url", help="a llama-server already running (then --model is not started)")
    args = ap.parse_args()
    proc = None
    if args.llm_url:
        llm = Llm(args.llm_url)
    else:
        proc = serve(args.model, args.llm_port)
        llm = Llm(f"http://127.0.0.1:{args.llm_port}")
    try:
        uvicorn.run(create_app(llm), host="127.0.0.1", port=args.port, log_level="warning")
    finally:
        if proc:
            proc.kill()


if __name__ == "__main__":
    main()
