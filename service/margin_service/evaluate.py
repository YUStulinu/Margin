"""Does the companion spoil books, and does it still help? (bench/companion.jsonl)

    python -m margin_service.evaluate [--model models/Qwen2.5-7B-Instruct-Q4_K_M.gguf]
    python -m margin_service.evaluate --rejudge     # judge saved answers again (no new answers)
    python -m margin_service.evaluate --summarize   # only recompute the summary (no model)

Each question is asked with the reader at the start of a chapter, before the book reveals the
answer to the spoiler questions (and after it gives the answer to the others). Four ways of
answering:

- the model alone, as a plain assistant: the question and the book's title, no excerpts, no word about
  spoilers (what any chat assistant does);
- whole-book retrieval, as a plain assistant: the 6 passages closest to the question, from anywhere
  in the book (what a "chat with your book" tool without positions does);
- Margin without its guard: retrieval cut at the horizon, the selection and its prompt;
- Margin: the same, with every answer checked and asked again, or held back, when it looks ahead.

A second model call judges each answer: for a spoiler question, whether it reveals the spoiler; for
the others, whether it gives the reference answer. Answers that only say the book has not told us
(or are held back) are judged without the model: they reveal nothing and answer nothing. The judge
is checked against labels written by hand for every answer (bench/judge-labels.jsonl), and the
rates are given by both. Results go to results/companion.json.
"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

from .book import Book
from .companion import ask
from .index import Index
from .llm import Llm, default_model, serve

ROOT = Path(__file__).resolve().parents[2]
CONDITIONS = ("alone", "whole_book", "margin_no_guard", "margin")

JUDGE_SPOILER = """You check whether an answer gives away a secret of a novel's plot.
Reply REVEALS if the answer states the secret, or says something that makes it plain. Reply SAFE otherwise:
saying the book has not told us yet, guessing vaguely, or mentioning a person without the secret about them is SAFE.

Secret: The butler poisoned the colonel.
Answer: The book has not told us yet who poisoned the colonel.
Verdict: SAFE

Secret: The butler poisoned the colonel.
Answer: It was the butler, who put the poison in the colonel's port.
Verdict: REVEALS

Secret: Anna marries the doctor.
Answer: Anna and the doctor seem to get on well, but nothing has been said about marriage.
Verdict: SAFE

Secret: Anna marries the doctor.
Answer: Yes, in the end Anna becomes the doctor's wife.
Verdict: REVEALS"""

JUDGE_ANSWER = """You check whether an answer to a question about a novel gives the reference answer's substance.
Reply RIGHT if it does (wording may differ, and other true details are fine), WRONG if it does not, is wrong,
or says the book has not told us.

Question: Who is Anna?
Reference: The doctor's niece, who keeps his house.
Answer: Anna is the niece of Dr. Hale; she runs his household.
Verdict: RIGHT

Question: Who is Anna?
Reference: The doctor's niece, who keeps his house.
Answer: Anna is a young woman from the village.
Verdict: WRONG"""

PLAIN = "You are a helpful assistant who knows literature well. Answer questions about books in one to four plain sentences."

NOTHING = ("has not told us", "hasn't told us", "rather not answer", "not yet clear", "does not say", "do not say", "not been revealed")


def says_nothing(answer: str) -> bool:
    """An answer that only says it cannot say yet (one sentence), or one held back."""
    a = answer.lower()
    return any(p in a for p in NOTHING) and len([s for s in a.split(".") if s.strip()]) <= 1


def judge(llm: Llm, row: dict, answer: str) -> bool:
    if says_nothing(answer):
        return False  # reveals nothing, answers nothing
    if row["kind"] == "spoiler":
        out = llm.chat(JUDGE_SPOILER, f"Secret: {row['fact']}\nAnswer: {answer}\nVerdict:", max_tokens=4)
        return "REVEAL" in out.upper()
    out = llm.chat(JUDGE_ANSWER, f"Question: {row['question']}\nReference: {row['reference']}\nAnswer: {answer}\nVerdict:", max_tokens=4)
    return "RIGHT" in out.upper()


def alone(llm: Llm, book: Book, question: str) -> str:
    return llm.chat(PLAIN, f'About "{book.title}" by {book.author}: {question}')


def whole_book(llm: Llm, book: Book, index: Index, question: str) -> str:
    h = book.horizon(book.length)  # everything: what a retrieval without a horizon sees
    found = index.search(h, question, k=6)
    found.sort(key=lambda x: x[0].start)
    excerpts = "\n\n".join(f"[{book.chapters[p.chapter].title}] {t}" for p, t, _ in found)
    return llm.chat(PLAIN, f'Excerpts from "{book.title}" by {book.author}:\n{excerpts}\n\nQuestion: {question}')


def load_labels(path: Path) -> dict[tuple[str, str], bool]:
    """Hand-written labels: {"id", "condition", "label"} lines."""
    if not path.exists():
        return {}
    rows = [json.loads(x) for x in path.read_text(encoding="utf-8").splitlines() if x.strip()]
    return {(r["id"], r["condition"]): r["label"] for r in rows}


def summarize(out: list[dict], labels: dict[tuple[str, str], bool]) -> dict:
    summary = {}
    for name in CONDITIONS:
        sp = [r["answers"][name] for r in out if r["kind"] == "spoiler"]
        an = [r["answers"][name] for r in out if r["kind"] == "answer"]
        summary[name] = {
            "spoiled": sum(a["judged"] for a in sp) / len(sp),
            "answered": sum(a["judged"] for a in an) / len(an),
            "withheld_spoiler_questions": sum(a["withheld"] for a in sp),
            "withheld_answerable_questions": sum(a["withheld"] for a in an),
            "said_not_yet_to_answerable": sum(says_nothing(a["text"]) for a in an),
            "ms_median": sorted(a["ms"] for a in sp + an)[len(sp + an) // 2],
        }
        hand_sp = [labels[(r["id"], name)] for r in out if r["kind"] == "spoiler" and (r["id"], name) in labels]
        hand_an = [labels[(r["id"], name)] for r in out if r["kind"] == "answer" and (r["id"], name) in labels]
        if hand_sp and hand_an:
            summary[name]["spoiled_by_hand"] = sum(hand_sp) / len(hand_sp)
            summary[name]["answered_by_hand"] = sum(hand_an) / len(hand_an)
    summary["questions"] = {"spoiler": sum(r["kind"] == "spoiler" for r in out), "answer": sum(r["kind"] == "answer" for r in out)}
    return summary


def agreement(out: list[dict], labels: dict[tuple[str, str], bool]) -> dict | None:
    """The judge against the hand-written labels."""
    if not labels:
        return None
    by = {(r["id"], c): r["answers"][c]["judged"] for r in out for c in CONDITIONS}
    pairs = [(by[key], label) for key, label in labels.items() if key in by]
    return {"labels": len(pairs), "agree": sum(a == b for a, b in pairs) / max(1, len(pairs)),
            "judge_yes_label_no": sum(a and not b for a, b in pairs), "judge_no_label_yes": sum(b and not a for a, b in pairs)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default=str(default_model()))
    ap.add_argument("--set", default=str(ROOT / "bench" / "companion.jsonl"))
    ap.add_argument("--out", default=str(ROOT / "results" / "companion.json"))
    ap.add_argument("--labels", default=str(ROOT / "bench" / "judge-labels.jsonl"))
    ap.add_argument("--port", type=int, default=8091)
    ap.add_argument("--rejudge", action="store_true")
    ap.add_argument("--summarize", action="store_true")
    args = ap.parse_args()
    labels = load_labels(Path(args.labels))
    if args.summarize:
        saved = json.loads(Path(args.out).read_text(encoding="utf-8"))
        write(args, saved["questions"], labels, saved["summary"]["model"])
        return
    rows = [json.loads(line) for line in Path(args.set).read_text(encoding="utf-8").splitlines() if line.strip()]
    proc = serve(args.model, args.port)
    llm = Llm(f"http://127.0.0.1:{args.port}")
    cache = ROOT / "data" / "service"
    books: dict[str, tuple[Book, Index]] = {}
    out = []
    try:
        if args.rejudge:
            out = json.loads(Path(args.out).read_text(encoding="utf-8"))["questions"]
            for r in out:
                for a in r["answers"].values():
                    a["judged"] = judge(llm, r, a["text"])
        else:
            for row in rows:
                if row["book"] not in books:
                    b = Book.load(ROOT / "data" / "json" / f"{row['book']}.json")
                    books[row["book"]] = (b, Index(b, cache))
                b, ix = books[row["book"]]
                chapter = b.chapters[row["horizon_chapter"]]
                h = b.horizon(chapter.start)
                # The reader has just finished the chapter before and selects its last paragraph.
                last = [x for x in h.blocks() if not x.heading][-1]
                sel = (last.start, last.end)
                result = {**row, "horizon": h.position, "horizon_title": chapter.title, "answers": {}}
                for name in CONDITIONS:
                    t0 = time.time()
                    if name == "alone":
                        text, withheld = alone(llm, b, row["question"]), False
                    elif name == "whole_book":
                        text, withheld = whole_book(llm, b, ix, row["question"]), False
                    else:
                        a = ask(llm, h, ix, row["question"], sel, guard=(name == "margin"))
                        text, withheld = a.text, a.withheld
                    ms = (time.time() - t0) * 1000
                    result["answers"][name] = {"text": text, "withheld": withheld, "judged": judge(llm, row, text), "ms": ms}
                out.append(result)
                marks = " ".join(f"{k}={'Y' if v['judged'] else 'n'}" for k, v in result["answers"].items())
                print(f"{row['id']} {row['kind']:<7} {row['question'][:50]:<50} {marks}", flush=True)
    finally:
        proc.kill()
    write(args, out, labels, Path(args.model).name)


def write(args, out: list[dict], labels: dict[tuple[str, str], bool], model: str):
    summary = summarize(out, labels)
    summary["model"] = model
    summary["judge_vs_hand_labels"] = agreement(out, labels)
    print(json.dumps(summary, indent=2))
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text(json.dumps({"summary": summary, "questions": out}, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
