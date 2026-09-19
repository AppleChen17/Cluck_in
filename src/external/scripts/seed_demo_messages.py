"""Demo scaffolding: push three realistic messages into a running service.

NOT production code, and nothing imports it. It exists so the whole auto-reply
loop can be demonstrated with no Gmail account, no Slack workspace and no Google
project -- the same reason the fixture adapter exists.

Why not just use the committed fixtures? Because they cannot be replied to. The
Slack fixture id is "slack:msg-001", which has no channel in it, and the Gmail
fixture has no address (ExternalMessage.sender is a display name by contract).
The messages here carry ids and metadata that route, so POST /reply works.

Three messages, on purpose:

  1. naming a meeting time      -> the chicken reacts and adds a calendar entry
  2. mentioning a meeting with no time -> the chicken does NOTHING
  3. an announcement            -> the chicken does NOTHING

Two of the three produce nothing, and that is the point. The second is the
sharper one: it talks about a meeting and still gets no calendar entry, because
it names no time. An assistant that answers everything is not trustworthy, and
one that invents a time it did not read is worse.

    .\\.venv\\Scripts\\python.exe src\\external\\scripts\\seed_demo_messages.py
    .\\.venv\\Scripts\\python.exe src\\external\\scripts\\seed_demo_messages.py --reply-to you@gmail.com

Requires EXTERNAL_DEBUG=true in src/external/.env, which gates /debug/inject.
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def messages(reply_to: str, channel: str, stamp: str, now: datetime) -> list[dict]:
    when = now.isoformat(timespec="seconds")
    return [
        {
            "id": "gmail:demo-noise-{}@example.com".format(stamp),
            "source": "gmail",
            "sender": "Alice Chen",
            "title": "上週會議紀錄",
            "content": (
                "你好，附件是上週的會議"
                "紀錄，有空再看就好，"
                "不急。"
            ),
            "timestamp": when,
            "unread": True,
            # The one thing a hand-written message must supply: the contract
            # carries a display name, never an address.
            "metadata": {"replyToAddress": reply_to},
        },
        {
            "id": "slack:{}:{}.000100".format(channel, stamp),
            "source": "slack",
            "sender": "Bob Lin",
            "title": None,
            "content": (
                "我們約在下週二下午三點"
                "開會，談 Cluck In 的進度，大概"
                "一小時，會議室 A。"
            ),
            "timestamp": when,
            "unread": True,
            "metadata": {"slackChannel": channel},
        },
        {
            "id": "slack:{}:{}.000200".format(channel, stamp),
            "source": "slack",
            "sender": "Carol Wu",
            "title": None,
            "content": (
                "提醒大家，下週一開始"
                "咖啡機移到二樓。"
            ),
            "timestamp": when,
            "unread": True,
            "metadata": {"slackChannel": channel},
        },
    ]


def inject(base_url: str, message: dict) -> None:
    body = json.dumps(message, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        base_url + "/debug/inject",
        data=body,
        headers={"Content-Type": "application/json; charset=utf-8"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=10) as response:
        json.loads(response.read().decode("utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--external-url", default="http://127.0.0.1:8100")
    parser.add_argument(
        "--reply-to",
        default="demo@example.com",
        help="where a reply to the email would go. Use your own address for a "
        "live demo; example.com is a reserved domain and goes nowhere.",
    )
    parser.add_argument("--channel", default="C_DEMO", help="Slack channel id to reply into")
    args = parser.parse_args()
    base_url = args.external_url.rstrip("/")

    # Unique per run, so the same three messages can be seeded again without the
    # buffer dedup silently swallowing them.
    stamp = str(int(time.time()))
    seeded = messages(args.reply_to, args.channel, stamp, datetime.now().astimezone())

    for message in seeded:
        try:
            inject(base_url, message)
        except urllib.error.HTTPError as exc:
            if exc.code == 404:
                print("POST /debug/inject returned 404.")
                print("Set EXTERNAL_DEBUG=true in src/external/.env and restart the service.")
                return 1
            print("HTTP {}: {}".format(exc.code, exc.read().decode("utf-8", "replace")))
            return 1
        except Exception as exc:  # noqa: BLE001
            print("Cannot reach {}: {}".format(base_url, exc))
            return 1
        print("injected {:<6} {}".format(message["source"], message["content"][:34]))

    print("\nSeeded 3 messages. Now run, in another terminal:")
    print("  .\\.venv\\Scripts\\python.exe src\\external\\scripts\\auto_reply_demo.py --once")
    print("Then look at {}/outbox".format(base_url))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
