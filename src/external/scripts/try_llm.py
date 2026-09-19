"""Probe: can a local LLM actually read what this module produces?

NOT production code. Nothing in src/external imports this, and it adds no
runtime dependency (stdlib urllib only). Its whole job is to answer one
question and hand the answer to whoever owns src/ai-engine:

    "Is gemma3:1b good enough to classify OUR messages, which are in Chinese?"

PRODUCT_SPEC_MVP.md section 12 calls model capability the single make-or-break
risk of the MVP and says to measure it on day one. This measures it.

Usage (from the repo root, with the venv python):

    python src/external/scripts/try_llm.py --fixtures
    python src/external/scripts/try_llm.py --task "Finish Cluck In prototype"
    python src/external/scripts/try_llm.py --model qwen2.5:3b-instruct --repeat 3
"""

import argparse
import json
import statistics
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

MODULE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(MODULE_ROOT))

# A Windows console defaults to a legacy code page (cp950 here), which cannot
# encode Chinese and raises UnicodeEncodeError mid-table. Everything this script
# prints is a message body, so force UTF-8 and never let encoding kill a run.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

from config import FIXTURES_DIR  # noqa: E402

# A hand-written FLAT schema, deliberately not ai-decision.schema.json.
# Ollama converts this into a sampling grammar, and its converter does not
# reliably support $ref/$defs. It also does NOT enforce minimum/maximum, so the
# 0..1 bounds are clamped in Python below rather than trusted here.
#
# Field order is generation order under a grammar constraint: "reason" first
# makes the model justify before it commits to a label, which is a free quality
# gain on a small model. Key order is irrelevant to consumers.
#
# messageId is deliberately absent: the contract needs it to match the message
# id exactly, and a 1b model asked to echo "slack:C08ABCDEF:1789788720.000200"
# will drop a digit. The caller injects it instead.
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


def build_user_prompt(message: dict, task: str, max_chars: int) -> str:
    content = message.get("content") or ""
    if len(content) > max_chars:
        content = content[:max_chars]
    return (
        "Current task: {task}\n"
        "From: {sender} (via {source})\n"
        "Subject: {title}\n"
        "Message:\n{content}"
    ).format(
        task=task or "(none selected)",
        sender=message.get("sender", "?"),
        source=message.get("source", "?"),
        title=message.get("title") or "(none)",
        content=content,
    )


def post_json(url: str, payload: dict, timeout: float) -> dict:
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url, data=body, headers={"Content-Type": "application/json"}, method="POST"
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def get_json(url: str, timeout: float) -> dict:
    with urllib.request.urlopen(url, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def clamp(value) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        return 0.0
    if number != number:  # NaN
        return 0.0
    return min(1.0, max(0.0, number))


def classify(base_url: str, model: str, message: dict, task: str, args) -> tuple[dict, float]:
    payload = {
        "model": model,
        "stream": False,
        # Ollama's default keep_alive is 5 minutes, so an idle gap between a
        # rehearsal and the real demo would go cold again.
        "keep_alive": "30m",
        "options": {
            "temperature": 0,
            "num_predict": 200,
            "num_ctx": args.num_ctx,
        },
        "format": AI_DECISION_FORMAT,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": build_user_prompt(message, task, args.max_chars)},
        ],
    }
    started = time.perf_counter()
    response = post_json(base_url + "/api/chat", payload, timeout=args.timeout)
    elapsed = time.perf_counter() - started

    raw = json.loads(response["message"]["content"])
    decision = {
        # Injected, never generated. This turns a correctness invariant into a
        # structural guarantee.
        "messageId": message["id"],
        "decision": raw["decision"],
        "relevance": clamp(raw["relevance"]),
        "urgency": clamp(raw["urgency"]),
        "reason": (raw.get("reason") or "").strip() or "No reason given.",
    }
    return decision, elapsed


def load_messages(args) -> list[dict]:
    if args.fixtures:
        messages = []
        for path in sorted(FIXTURES_DIR.glob("external-message.*.json")):
            messages.append(json.loads(path.read_text(encoding="utf-8")))
        return messages
    url = "{}/messages?limit={}".format(args.external_url.rstrip("/"), args.limit)
    return get_json(url, timeout=10)["messages"]


def warm_up(base_url: str, model: str) -> None:
    """The first call after a cold start loads ~800MB from disk.

    Without this every first request blows a 2s budget and looks like a bug.
    """
    print("warming up {} ...".format(model), end=" ", flush=True)
    started = time.perf_counter()
    try:
        post_json(
            base_url + "/api/chat",
            {
                "model": model,
                "stream": False,
                "keep_alive": "30m",
                "messages": [{"role": "user", "content": "hi"}],
                "options": {"num_predict": 1},
            },
            timeout=120,
        )
        print("warm in {:.1f}s".format(time.perf_counter() - started))
    except Exception as exc:  # noqa: BLE001
        print("FAILED: {}".format(exc))
        raise


def ellipsize(text: str, width: int) -> str:
    text = " ".join(str(text).split())
    return text if len(text) <= width else text[: width - 1] + "…"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="gemma3:1b")
    parser.add_argument("--task", default="Finish Cluck In prototype")
    # 127.0.0.1, never "localhost". Ollama binds IPv4 only, and on Windows
    # "localhost" resolves to ::1 first; that connection fails and the retry on
    # IPv4 lands ~2.05s later. Measured on this machine: qwen2.5:3b takes 4.65s
    # via localhost and 3.12s via 127.0.0.1, for an identical request.
    parser.add_argument("--ollama-url", default="http://127.0.0.1:11434")
    parser.add_argument("--external-url", default="http://127.0.0.1:8100")
    parser.add_argument(
        "--fixtures",
        action="store_true",
        help="read shared/fixtures instead of the running external service",
    )
    parser.add_argument("--limit", type=int, default=20)
    parser.add_argument("--repeat", type=int, default=1, help="runs per message, for latency stats")
    # 400, not 1200. Measured on a real 999-character Notion newsletter with
    # qwen2.5:3b: 1200 chars takes 6.08s, 300 takes 3.21s, and the verdict is
    # identical at every step down to 150. Prompt evaluation dominates latency
    # on long mail, and the subject plus opening lines usually carry the signal.
    parser.add_argument("--max-chars", type=int, default=400, dest="max_chars")
    parser.add_argument(
        "--num-ctx",
        type=int,
        default=2048,
        dest="num_ctx",
        help="raise this if you raise --max-chars, or the prompt is silently "
        "truncated from the left and the model classifies a fragment",
    )
    parser.add_argument("--timeout", type=float, default=60.0)
    args = parser.parse_args()

    try:
        tags = get_json(args.ollama_url.rstrip("/") + "/api/tags", timeout=5)
    except Exception as exc:  # noqa: BLE001
        print("Ollama is not reachable at {} ({}).".format(args.ollama_url, exc))
        print("Install it with:  winget install --id Ollama.Ollama -e")
        return 2

    available = {m["name"] for m in tags.get("models", [])}
    if args.model not in available and available:
        print("Model {!r} is not pulled. Available: {}".format(args.model, ", ".join(sorted(available))))
        print("Pull it with:  ollama pull {}".format(args.model))
        return 2

    try:
        messages = load_messages(args)
    except Exception as exc:  # noqa: BLE001
        print("Could not load messages ({}).".format(exc))
        print("Either start the external service, or pass --fixtures.")
        return 2

    if not messages:
        print("No messages to classify.")
        return 1

    base_url = args.ollama_url.rstrip("/")
    warm_up(base_url, args.model)

    print()
    print("model={}  task={!r}  messages={}  repeat={}".format(
        args.model, args.task, len(messages), args.repeat))
    print("-" * 118)
    print("{:<26} {:<7} {:>5} {:>5} {:>7}  {}".format(
        "MESSAGE", "VERDICT", "REL", "URG", "SEC", "REASON"))
    print("-" * 118)

    latencies: list[float] = []
    failures = 0
    interrupted = False
    try:
        for message in messages:
            for _ in range(args.repeat):
                try:
                    decision, elapsed = classify(base_url, args.model, message, args.task, args)
                except KeyboardInterrupt:
                    raise
                except Exception as exc:  # noqa: BLE001
                    failures += 1
                    print("{:<26} {:<7} {:>5} {:>5} {:>7}  {}".format(
                        ellipsize(message.get("content") or message["id"], 26),
                        "ERROR", "-", "-", "-", "{}: {}".format(type(exc).__name__, exc)))
                    continue
                latencies.append(elapsed)
                print("{:<26} {:<7} {:>5.2f} {:>5.2f} {:>7.2f}  {}".format(
                    ellipsize(message.get("content") or message["id"], 26),
                    decision["decision"],
                    decision["relevance"],
                    decision["urgency"],
                    elapsed,
                    ellipsize(decision["reason"], 50),
                ))
    except KeyboardInterrupt:
        # Ctrl+C stops the run and still prints the stats gathered so far,
        # rather than dumping a traceback and losing them.
        interrupted = True
        print()
        print("Interrupted. Showing results for the {} message(s) already done.".format(
            len(latencies)))

    print("-" * 118)
    if latencies:
        ordered = sorted(latencies)
        p95 = ordered[min(len(ordered) - 1, int(len(ordered) * 0.95))]
        print("latency  p50={:.2f}s  p95={:.2f}s  max={:.2f}s  n={}".format(
            statistics.median(ordered), p95, ordered[-1], len(ordered)))
        # PRODUCT_SPEC_MVP.md success criterion 2 gives Pat a 3 second budget.
        if p95 > 2.0:
            print("WARNING: p95 exceeds the 2s budget assumed for the Pat flow.")
    if failures:
        print("{} call(s) failed.".format(failures))

    print()
    print("Note for whoever wires this into src/ai-engine: Ollama serialises")
    print("requests per model unless OLLAMA_NUM_PARALLEL is set, so a 10-message")
    print("Pat batch costs 10x this latency, not 1x.")
    if interrupted:
        return 130  # conventional exit code for SIGINT
    return 0 if not failures else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        # Ctrl+C during warm-up or while loading messages, i.e. before the run
        # loop has its own handler.
        print("\nInterrupted before the run started.")
        raise SystemExit(130) from None
