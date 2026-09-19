"""Routing data: what the contract deliberately does not carry.

The recurring theme: ExternalMessage.sender is a display name by contract, so
an email address can never be recovered from a delivered message. Everything
here exists because of that one sentence in docs/data-contracts.md.
"""

from adapters.gmail_adapter import build_reply_target as gmail_target
from adapters.slack_adapter import build_reply_target as slack_target
from reply_registry import ReplyRegistry, ReplyTarget, target_from_message
from schemas import ExternalMessage

RAW = (
    b"From: Alice Chen <alice@example.com>\r\n"
    b"Subject: Demo checklist\r\n"
    b"Message-ID: <abc123@mail.example.com>\r\n"
    b"References: <root@mail.example.com>\r\n"
    b"Date: Sat, 19 Sep 2026 11:31:00 +0800\r\n"
    b"\r\n"
    b"When are you free next week?\r\n"
)


# -- the registry -------------------------------------------------------------


def test_a_target_is_found_by_message_id():
    registry = ReplyRegistry()
    registry.remember(ReplyTarget(message_id="gmail:m1", source="gmail", address="a@b.c"))
    assert registry.get("gmail:m1").address == "a@b.c"
    assert registry.get("gmail:missing") is None


def test_the_registry_is_bounded():
    """Gmail re-fetches the same unread mail forever; unbounded means unbounded."""
    registry = ReplyRegistry(maxlen=2)
    for i in range(4):
        registry.remember(ReplyTarget(message_id="m{}".format(i), source="slack", channel="C"))
    assert len(registry) == 2
    assert registry.get("m0") is None
    assert registry.get("m3") is not None


def test_re_seeing_a_message_keeps_it_alive():
    """The newest messages are the ones anyone replies to."""
    registry = ReplyRegistry(maxlen=2)
    registry.remember(ReplyTarget(message_id="old", source="slack", channel="C"))
    registry.remember(ReplyTarget(message_id="new", source="slack", channel="C"))
    registry.remember(ReplyTarget(message_id="old", source="slack", channel="C"))
    registry.remember(ReplyTarget(message_id="newest", source="slack", channel="C"))
    assert registry.get("old") is not None
    assert registry.get("new") is None


def test_the_destination_is_whichever_field_the_source_uses():
    assert ReplyTarget("m", "gmail", address="a@b.c").destination() == "a@b.c"
    assert ReplyTarget("m", "slack", channel="C1").destination() == "C1"
    assert ReplyTarget("m", "gmail").destination() == ""


# -- gmail: from the raw message ----------------------------------------------


def test_a_gmail_target_carries_the_threading_chain():
    target = gmail_target(RAW, "gmail:abc123@mail.example.com")
    assert target.address == "alice@example.com"
    assert target.rfc_message_id == "<abc123@mail.example.com>"
    assert target.references == "<root@mail.example.com>"
    assert target.subject == "Demo checklist"


def test_reply_to_beats_from():
    """Mailing lists and ticketing systems set it; answering From misses."""
    raw = RAW.replace(b"From: ", b"Reply-To: team@example.com\r\nFrom: ")
    assert gmail_target(raw, "x").address == "team@example.com"


def test_an_address_only_from_header_still_works():
    raw = RAW.replace(b"From: Alice Chen <alice@example.com>", b"From: alice@example.com")
    assert gmail_target(raw, "x").address == "alice@example.com"


def test_an_rfc2047_encoded_sender_name_does_not_break_the_address():
    raw = RAW.replace(
        b"From: Alice Chen <alice@example.com>",
        b"From: =?UTF-8?B?6Zmz5bCP5aec?= <alice@example.com>",
    )
    assert gmail_target(raw, "x").address == "alice@example.com"


def test_no_address_means_no_target_rather_than_a_bad_one():
    assert gmail_target(b"Subject: x\r\n\r\nbody\r\n", "x") is None


# -- slack: from the event ----------------------------------------------------


def test_a_loose_message_threads_under_itself():
    """thread_ts falling back to ts is what starts a thread instead of adding
    another loose message beside it."""
    target = slack_target({"channel": "C1", "ts": "1789788720.000200"}, "slack:C1:1789788720.000200")
    assert target.thread_ts == target.ts == "1789788720.000200"


def test_a_thread_reply_stays_in_its_own_thread():
    target = slack_target(
        {"channel": "C1", "ts": "1789788999.000100", "thread_ts": "1789788720.000200"}, "id"
    )
    assert target.thread_ts == "1789788720.000200"
    assert target.ts == "1789788999.000100"


# -- recovered from a delivered message ---------------------------------------


def test_a_slack_id_alone_is_enough_to_reply():
    """"slack:<channel>:<ts>" is exactly what chat.postMessage wants."""
    message = ExternalMessage(
        id="slack:C08ABCDEF:1789788720.000200",
        source="slack",
        sender="Bob",
        content="hi",
        timestamp="2026-09-19T11:32:00+08:00",
        unread=True,
    )
    target = target_from_message(message)
    assert target.channel == "C08ABCDEF"
    assert target.ts == "1789788720.000200"


def test_the_committed_slack_fixture_has_no_channel_to_route_to(load_fixture):
    """Its id is "slack:msg-001" -- documented, and why the demo injects its own."""
    message = ExternalMessage.model_validate(load_fixture("external-message.slack.json"))
    assert target_from_message(message) is None


def test_a_gmail_message_needs_an_explicit_address():
    base = dict(
        id="gmail:abc@mail.example.com",
        source="gmail",
        sender="Alice Chen",
        title="Hi",
        content="x",
        timestamp="2026-09-19T11:31:00+08:00",
        unread=True,
    )
    assert target_from_message(ExternalMessage(**base)) is None
    with_address = ExternalMessage(**base, metadata={"replyToAddress": "alice@example.com"})
    target = target_from_message(with_address)
    assert target.address == "alice@example.com"
    # Reconstructed from the id, so an injected reply still threads.
    assert target.rfc_message_id == "<abc@mail.example.com>"
