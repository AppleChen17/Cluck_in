"""Standalone: is this machine fast enough to run the classifier?

Self-contained on purpose. Copy this one file to another machine, install
Ollama and `ollama pull qwen2.5:3b-instruct`, and run it with any Python 3.9+.
No repository, no virtualenv, no dependencies beyond the standard library.

    python check_machine.py
    python check_machine.py --model gemma3:1b

It reports where the time actually goes, so a slow result can be attributed
rather than guessed at.
"""

import argparse
import json
import statistics
import sys
import time
import urllib.error
import urllib.request

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

# Matches src/external/scripts/try_llm.py. reason is generated FIRST on purpose:
# under a grammar constraint, field order is generation order, and making the
# model justify before it labels is what keeps the judgement usable.
AI_DECISION_FORMAT = {
    "type": "object",
    "properties": {
        "reason": {"type": "string"},
        "relevance": {"type": "number"},
        "urgency": {"type": "number"},
        "decision": {"type": "string", "enum": ["urgent", "allow", "hold"]},
    },
    "required": ["reason", "relevance", "urgency", "decision"],
}

SYSTEM_PROMPT = """You classify one incoming message for a focus-mode assistant.
The user is working on ONE task. Decide whether the message deserves interrupting them.

urgent = must see right now. allow = fine to see normally. hold = defer until focus ends.
relevance (0-1) = how related the message is to the current task.
urgency (0-1) = how time-sensitive it is.
reason = ONE short sentence, in the SAME LANGUAGE as the message, explaining your choice.
When unsure, prefer hold."""

CASES = [
    ("urgent", "Cluck In 的 build 掛了，prototype 現在跑不起來"),
    ("urgent", "你的 PR 有 conflict，麻煩 rebase 一下才能 merge"),
    ("quiet", "good morning"),
    ("quiet", "中午要不要一起吃飯？"),
    ("quiet", "週末有人要去看電影嗎"),
]
TASK = "Finish Cluck In prototype"


def post(url, payload, timeout):
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url, data=body, headers={"Content-Type": "application/json"}, method="POST"
    )
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def get(url, timeout):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def classify(base, model, text, num_ctx):
    user = (
        "Current task: {}\nFrom: Bob (via slack)\nSubject: (none)\nMessage:\n{}"
    ).format(TASK, text[:400])
    payload = {
        "model": model,
        "stream": False,
        "keep_alive": "30m",
        "options": {"temperature": 0, "num_predict": 200, "num_ctx": num_ctx},
        "format": AI_DECISION_FORMAT,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": user},
        ],
    }
    started = time.perf_counter()
    r = post(base + "/api/chat", payload, 300)
    wall = time.perf_counter() - started
    return json.loads(r["message"]["content"]), wall, r


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", default="qwen2.5:3b-instruct")
    # 127.0.0.1, never "localhost": Ollama binds IPv4 only, and on Windows
    # "localhost" tries ::1 first, costing about 2 seconds on every request.
    p.add_argument("--url", default="http://127.0.0.1:11434")
    p.add_argument("--num-ctx", type=int, default=2048, dest="num_ctx")
    args = p.parse_args()
    base = args.url.rstrip("/")

    try:
        tags = get(base + "/api/tags", 10)
    except Exception as exc:  # noqa: BLE001
        print("Ollama is not reachable at {} ({}).".format(base, exc))
        print("Install:  winget install --id Ollama.Ollama -e")
        return 2

    names = {m["name"] for m in tags.get("models", [])}
    if args.model not in names:
        print("Model {!r} is not pulled.".format(args.model))
        print("Available: {}".format(", ".join(sorted(names)) or "(none)"))
        print("Pull it:  ollama pull {}".format(args.model))
        return 2

    # Prove the localhost penalty exists on this machine too, cheaply.
    print("== connection check ==")
    for host in ("http://127.0.0.1:11434", "http://localhost:11434"):
        try:
            s = time.perf_counter()
            get(host + "/api/tags", 10)
            print("  {:<26} {:>6.0f} ms".format(host, (time.perf_counter() - s) * 1000))
        except Exception as exc:  # noqa: BLE001
            print("  {:<26} unreachable ({})".format(host, type(exc).__name__))

    print()
    print("== warming up {} ==".format(args.model))
    s = time.perf_counter()
    classify(base, args.model, "hi", args.num_ctx)
    print("  first call (includes model load): {:.1f}s".format(time.perf_counter() - s))

    print()
    print("== classification ==")
    print("  {:<34} {:<7} {:>5} {:>5} {:>8}".format("message", "verdict", "rel", "urg", "sec"))
    print("  " + "-" * 66)
    walls, hits, last = [], 0, None
    for expect, text in CASES:
        d, wall, raw = classify(base, args.model, text, args.num_ctx)
        walls.append(wall)
        last = raw
        ok = (d["decision"] == "urgent") if expect == "urgent" else (d["decision"] != "urgent")
        hits += ok
        print("  {} {:<32} {:<7} {:>5.2f} {:>5.2f} {:>7.2f}s".format(
            "OK" if ok else "??", text[:30], d["decision"], d["relevance"], d["urgency"], wall))

    ordered = sorted(walls)
    print()
    print("== results ==")
    print("  latency   p50 {:.2f}s   max {:.2f}s".format(statistics.median(ordered), ordered[-1]))
    print("  labels    {}/{} matched a hand-written expectation".format(hits, len(CASES)))

    ns = 1e9
    print()
    print("== where the time goes (last call) ==")
    print("  load          {:>7.2f}s".format(last.get("load_duration", 0) / ns))
    print("  prompt eval   {:>7.2f}s   ({} tokens)".format(
        last["prompt_eval_duration"] / ns, last["prompt_eval_count"]))
    gen_s = last["eval_duration"] / ns
    print("  generation    {:>7.2f}s   ({} tokens, {:.1f} tok/s)".format(
        gen_s, last["eval_count"], last["eval_count"] / gen_s if gen_s else 0))
    print("  ollama total  {:>7.2f}s".format(last["total_duration"] / ns))

    print()
    print("== reference: Intel Core Ultra 7 155H, CPU only ==")
    print("  qwen2.5:3b-instruct   p50 3.89s   p95 7.35s   generation 19.0 tok/s")
    print()
    print("Generation speed is bounded by memory bandwidth, so it is the number")
    print("to compare. Check `ollama ps` shows 100% GPU if a discrete card is present.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\nInterrupted.")
        raise SystemExit(130) from None
