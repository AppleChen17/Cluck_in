"""Demo glue: watch the inbox, decide, answer. NOT production code.

Nothing in src/external imports this and it adds no runtime dependency (stdlib
urllib only), exactly like scripts/try_llm.py. It exists to answer one question
on its own, before the C# side is ready:

    "What does the auto-reply loop actually look like end to end?"

It is deliberately NOT a stand-in for src/app. The real product decides in the
C# state machine, with the focus session and the current task in hand, and calls
the same HTTP endpoints this script calls. This is a straight line through them
so the feature can be seen working and rehearsed today.

    GET  /messages                  what arrived
      -> classify intent            local model, or keywords if it is not running
    asking when you are free:
      POST /calendar/availability   real free slots, computed not guessed
      POST /reply                   answer in the same thread / mail thread
    telling you about a meeting:
      POST /react                   a thumbs up is a real answer
      POST /calendar/events         put it on the calendar

Usage, from the repo root, with the service already running on :8100:

    .\\.venv\\Scripts\\python.exe src\\external\\scripts\\auto_reply_demo.py
    .\\.venv\\Scripts\\python.exe src\\external\\scripts\\auto_reply_demo.py --once
    .\\.venv\\Scripts\\python.exe src\\external\\scripts\\auto_reply_demo.py --no-llm

Sending obeys EXTERNAL_SEND_DRY_RUN, which is true by default: every reply below
is routed and recorded and nothing leaves the machine. Watch GET /outbox to see
what it would have sent. Flip that flag only once the allowlist is set.
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta
from pathlib import Path

MODULE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(MODULE_ROOT))

# The console here defaults to a legacy code page that cannot encode Chinese,
# and every message body this prints is Chinese. Never let output kill a run.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


# -- intent classification ----------------------------------------------------

# Flat, with every field required. Ollama turns this into a sampling grammar and
# its converter handles neither $ref nor optional properties reliably; "" and 0
# stand in for absent. `reason` is generated first on purpose -- making a small
# model justify before it commits to a label is a free quality gain.
INTENT_FORMAT = {
    "type": "object",
    "properties": {
        "reason": {"type": "string"},
        "intent": {
            "type": "string",
            "enum": ["asking_availability", "meeting_invite", "other"],
        },
        "startTime": {"type": "string"},
        "durationMinutes": {"type": "number"},
        "title": {"type": "string"},
    },
    "required": ["reason", "intent", "startTime", "durationMinutes", "title"],
}

SYSTEM_PROMPT = """You read one incoming message and label what the sender wants.

asking_availability = they are ASKING when the reader is free, or asking to find
  a time. They have not named a specific time yet.
meeting_invite = they are TELLING the reader about a specific time that is
  already decided, or proposing one concrete time.
other = anything else at all. When unsure, answer other.

For meeting_invite only, also extract:
  startTime = the meeting start as an ISO 8601 datetime with the SAME offset as
    "Now" below. If the message does not give a specific date and time, return "".
  durationMinutes = the stated length, or 60 if it is not stated.
  title = a short title for the calendar entry, in the message's own language.
For every other intent return "" , 0 and "".

reason = one short sentence in the message's own language."""


def build_prompt(message: dict, now: datetime) -> str:
    """The model cannot know what day it is, and will invent one if not told."""
    weekday = "一二三四五六日"[now.weekday()]
    return (
        "Now: {now} (星期{weekday})\n"
        "From: {sender} (via {source})\n"
        "Subject: {title}\n"
        "Message:\n{content}"
    ).format(
        now=now.isoformat(timespec="minutes"),
        weekday=weekday,
        sender=message.get("sender", "?"),
        source=message.get("source", "?"),
        title=message.get("title") or "(none)",
        content=(message.get("content") or "")[:1200],
    )


# The fallback when Ollama is not running. Crude on purpose: it is there so the
# demo degrades to something visible instead of dying on stage, not so anyone
# ships it. A question mark next to a time word is most of the signal.
_ASKING_HINTS = (
    "什麼時候有空", "有空嗎", "方便嗎",
    "什麼時間方便", "約個時間", "約時間",
    "when are you free", "are you available", "what time works", "find a time",
)
_INVITE_HINTS = (
    "開會", "會議", "約在", "定在", "meeting",
    "let us meet", "scheduled for", "安排在",
)


def classify_by_keywords(message: dict) -> dict:
    text = "{} {}".format(message.get("title") or "", message.get("content") or "").lower()
    for hint in _ASKING_HINTS:
        if hint.lower() in text:
            return {
                "intent": "asking_availability",
                "reason": "keyword fallback: {!r}".format(hint),
                "startTime": "",
                "durationMinutes": 0,
                "title": "",
            }
    for hint in _INVITE_HINTS:
        if hint.lower() in text:
            # No time extraction without a model: say so rather than guess. The
            # loop below turns this into a reaction and no calendar entry.
            return {
                "intent": "meeting_invite",
                "reason": "keyword fallback: {!r}".format(hint),
                "startTime": "",
                "durationMinutes": 60,
                "title": (message.get("title") or "Meeting")[:60],
            }
    return {"intent": "other", "reason": "keyword fallback: no hint matched",
            "startTime": "", "durationMinutes": 0, "title": ""}


# -- plain HTTP ---------------------------------------------------------------


def request_json(url: str, payload=None, timeout: float = 30, method: str | None = None):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "application/json; charset=utf-8"},
        method=method or ("POST" if data else "GET"),
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", "replace")
        raise RuntimeError("HTTP {} from {}: {}".format(exc.code, url, body)) from exc


def classify(message: dict, args) -> dict:
    if args.no_llm:
        return classify_by_keywords(message)
    payload = {
        "model": args.model,
        "stream": False,
        # Ollama unloads after 5 minutes by default, so a gap between the
        # rehearsal and the real demo would go cold again.
        "keep_alive": "30m",
        "options": {"temperature": 0, "num_predict": 250, "num_ctx": 4096},
        "format": INTENT_FORMAT,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": build_prompt(message, datetime.now().astimezone())},
        ],
    }
    try:
        response = request_json(args.ollama_url.rstrip("/") + "/api/chat", payload, timeout=args.timeout)
        return json.loads(response["message"]["content"])
    except Exception as exc:  # noqa: BLE001
        print("  ! model unavailable ({}); falling back to keywords".format(exc))
        return classify_by_keywords(message)


# -- the three things it can do -----------------------------------------------


def answer_with_availability(message: dict, args) -> None:
    result = request_json(
        args.external_url + "/calendar/availability",
        {"durationMinutes": args.duration, "withinDays": args.within_days, "limit": 3},
    )
    # Composed from a template, not generated. The slots are the part that must
    # be right, and they came from real calendar arithmetic; letting a 3b model
    # rewrite them is how 14:00 becomes 15:00.
    body = "你好，這幾個時段我可以：\n{}\n\n方便的話回我一句，我再發會議邀請。\n\n（由 Cluck In 自動回覆）".format(
        result["text"]
    )
    print("  slots: {}".format(result["text"].replace("\n", " / ")))
    sent = request_json(
        args.external_url + "/reply", {"messageId": message["id"], "body": body}
    )
    _report_send(sent)


def acknowledge_meeting(message: dict, intent: dict, args) -> None:
    if message["source"] == "slack":
        try:
            reaction = request_json(
                args.external_url + "/react",
                {"messageId": message["id"], "emoji": "\U0001F44D"},
            )
            print("  reacted: {} (delivered={})".format(
                reaction["emoji"], reaction["delivered"]))
        except RuntimeError as exc:
            print("  ! reaction failed: {}".format(exc))

    start = _parse_start(intent.get("startTime"))
    if start is None:
        # Honest about the limit rather than inventing a time. A meeting on the
        # wrong day is worse than no meeting.
        print("  no usable start time in the message; not creating an event")
        return
    minutes = int(intent.get("durationMinutes") or 60)
    created = request_json(
        args.external_url + "/calendar/events",
        {
            "title": intent.get("title") or (message.get("title") or "Meeting"),
            "startTime": start.isoformat(timespec="seconds"),
            "endTime": (start + timedelta(minutes=minutes)).isoformat(timespec="seconds"),
            "description": "{}: {}".format(
                message.get("sender", "?"), (message.get("content") or "")[:300]
            ),
            "fromMessageId": message["id"],
        },
    )
    event = created["events"][0]
    print("  calendar [{}]: {} {}".format(created["backend"], event["startTime"], event["title"]))


def _parse_start(value) -> datetime | None:
    """Trust the model's date only as far as it can be validated."""
    if not value or not isinstance(value, str):
        return None
    text = value.strip()
    if text.endswith(("Z", "z")):
        text = text[:-1] + "+00:00"
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        # A naive value is the model omitting the offset, not a UTC time.
        parsed = parsed.replace(tzinfo=datetime.now().astimezone().tzinfo)
    if parsed < datetime.now().astimezone() - timedelta(hours=1):
        # Models reach for the current year and last week's weekday. A meeting
        # in the past is always an extraction error.
        print("  extracted start time {} is in the past; ignoring it".format(parsed))
        return None
    return parsed


def _report_send(sent: dict) -> None:
    if sent.get("duplicate"):
        print("  already answered earlier (id {}); nothing sent".format(sent["id"]))
    elif sent.get("dryRun"):
        print("  DRY RUN -> {}: {}".format(sent["target"], sent["preview"]))
    elif sent.get("error"):
        print("  ! send failed: {}".format(sent["error"]))
    else:
        print("  SENT -> {}: {}".format(sent["target"], sent["preview"]))


# -- the loop -----------------------------------------------------------------


def handle(message: dict, args) -> None:
    label = "{} from {}: {}".format(
        message["source"], message["sender"],
        (message.get("title") or message.get("content") or "")[:60].replace("\n", " "),
    )
    print("\n* {}".format(label))
    intent = classify(message, args)
    print("  intent={} ({})".format(intent.get("intent"), intent.get("reason", "")[:80]))

    try:
        if intent.get("intent") == "asking_availability":
            answer_with_availability(message, args)
        elif intent.get("intent") == "meeting_invite":
            acknowledge_meeting(message, intent, args)
        else:
            print("  nothing to do")
    except RuntimeError as exc:
        # One bad message must never stop the loop, least of all mid-demo.
        print("  ! {}".format(exc))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--external-url", default="http://127.0.0.1:8100")
    # 127.0.0.1, never "localhost": Ollama binds IPv4 only, and on Windows
    # "localhost" resolves to ::1 first, costing ~2s per request on the retry.
    # Measured in docs/llm-findings.md.
    parser.add_argument("--ollama-url", default="http://127.0.0.1:11434")
    parser.add_argument("--model", default="qwen2.5:3b-instruct")
    parser.add_argument("--no-llm", action="store_true", help="keyword classifier only")
    parser.add_argument("--once", action="store_true", help="one pass, then exit")
    parser.add_argument("--interval", type=float, default=5.0)
    parser.add_argument("--timeout", type=float, default=60.0)
    parser.add_argument("--duration", type=int, default=30, help="meeting length to offer")
    parser.add_argument("--within-days", type=int, default=5)
    args = parser.parse_args()
    args.external_url = args.external_url.rstrip("/")

    try:
        health = request_json(args.external_url + "/health", timeout=5)
    except Exception as exc:  # noqa: BLE001
        print("Cannot reach the external service at {}: {}".format(args.external_url, exc))
        print("Start it first:\n  .\\.venv\\Scripts\\python.exe -m uvicorn app:app "
              "--app-dir src\\external --port 8100 --workers 1")
        return 1

    sending = health.get("sending") or {}
    print("external: adapters={} calendar={} dryRun={} allowlisted={}".format(
        [a["name"] for a in health.get("adapters", [])],
        (health.get("calendar") or {}).get("backend"),
        sending.get("dryRun"),
        sending.get("allowlisted"),
    ))
    if sending.get("dryRun"):
        print("Nothing will actually be sent. Watch {}/outbox.".format(args.external_url))
    else:
        print("*** LIVE. Replies will really be sent. ***")
    print("classifier: {}".format("keywords only" if args.no_llm else args.model))

    cursor = None
    while True:
        url = args.external_url + "/messages?limit=20"
        if cursor:
            url += "&cursor=" + urllib.parse.quote(cursor)
        try:
            envelope = request_json(url, timeout=15)
        except Exception as exc:  # noqa: BLE001
            print("poll failed: {}".format(exc))
            if args.once:
                return 1
            time.sleep(args.interval)
            continue

        if envelope.get("replayed"):
            # The service restarted. Its reply registry went with it, so old
            # messages cannot be answered by id -- and /reply says so clearly
            # rather than sending to the wrong place.
            print("(service restarted; replaying what it still has)")
        for message in envelope["messages"]:
            handle(message, args)
        cursor = envelope["cursor"]

        if args.once:
            return 0
        time.sleep(args.interval)


if __name__ == "__main__":
    raise SystemExit(main())
