"""The outbound guards, and the pure message building underneath them.

No network and no credentials: a fake Sender records what it was asked to
deliver, which is the only thing worth asserting about the layer above it.
"""

import pytest

from config import Settings
from sender.base import Dispatcher, SendBlocked, Sender, SendUnavailable
from sender.gmail_sender import GmailSender, build_email, build_references, build_reply_subject
from sender.outbox import Outbox
from sender.slack_sender import emoji_to_name


class FakeSender(Sender):
    source = "gmail"

    def __init__(self, available: bool = True, failure: Exception | None = None) -> None:
        self.calls: list[dict] = []
        self._available = available
        self._failure = failure

    def available(self) -> bool:
        return self._available

    def deliver(self, **kwargs) -> str:
        self.calls.append(kwargs)
        if self._failure is not None:
            raise self._failure
        return "provider-id-1"


def make(dry_run: bool = True, allowlist: str = "", **sender_kwargs):
    cfg = Settings(
        _env_file=None,
        external_send_dry_run=dry_run,
        external_send_allowlist=allowlist,
    )
    outbox = Outbox(maxlen=20)
    sender = FakeSender(**sender_kwargs)
    return cfg, outbox, sender, Dispatcher(cfg, outbox, {"gmail": sender})


# -- the dry run --------------------------------------------------------------


def test_dry_run_records_everything_and_delivers_nothing():
    """The point of the default: fully routed, fully visible, nothing sent."""
    _, outbox, sender, dispatcher = make(dry_run=True)
    result = dispatcher.dispatch(
        source="gmail", destination="alice@example.com", body="hello there"
    )
    assert sender.calls == []
    assert result.dryRun is True
    assert result.delivered is False
    assert result.error is None
    assert result.target == "alice@example.com"
    assert outbox.recent()[0].id == result.id


def test_a_dry_run_ignores_the_allowlist():
    """Rehearsing against a destination not yet allowlisted is what it is for."""
    _, _, _, dispatcher = make(dry_run=True, allowlist="someone@else.com")
    result = dispatcher.dispatch(source="gmail", destination="alice@example.com", body="x")
    assert result.dryRun is True


# -- the allowlist ------------------------------------------------------------


def test_a_real_send_outside_the_allowlist_is_refused():
    """Refused, never quietly downgraded: the caller must learn nothing went out."""
    _, outbox, sender, dispatcher = make(dry_run=False, allowlist="boss@example.com")
    with pytest.raises(SendBlocked):
        dispatcher.dispatch(source="gmail", destination="alice@example.com", body="x")
    assert sender.calls == []
    assert outbox.recent() == []


def test_the_allowlist_ignores_case_and_padding():
    _, _, sender, dispatcher = make(dry_run=False, allowlist=" Alice@Example.com , C123 ")
    dispatcher.dispatch(source="gmail", destination="alice@example.COM", body="x")
    assert len(sender.calls) == 1


def test_an_empty_allowlist_allows_everything():
    _, _, sender, dispatcher = make(dry_run=False, allowlist="")
    dispatcher.dispatch(source="gmail", destination="anyone@example.com", body="x")
    assert len(sender.calls) == 1


# -- real sends ---------------------------------------------------------------


def test_a_real_send_passes_provider_arguments_through():
    _, _, sender, dispatcher = make(dry_run=False)
    result = dispatcher.dispatch(
        source="gmail",
        destination="alice@example.com",
        body="body text",
        subject="Re: Demo",
        in_reply_to="<abc@mail>",
    )
    assert result.delivered is True
    assert result.providerId == "provider-id-1"
    assert sender.calls[0]["subject"] == "Re: Demo"
    assert sender.calls[0]["in_reply_to"] == "<abc@mail>"


def test_an_unconfigured_sender_refuses_a_real_send():
    _, _, _, dispatcher = make(dry_run=False, available=False)
    with pytest.raises(SendUnavailable):
        dispatcher.dispatch(source="gmail", destination="alice@example.com", body="x")


def test_an_unknown_source_is_unavailable_not_a_crash():
    _, _, _, dispatcher = make()
    with pytest.raises(SendUnavailable):
        dispatcher.dispatch(source="carrier-pigeon", destination="x", body="y")


def test_a_provider_failure_is_reported_in_the_result_not_raised():
    """A failed reply belongs on the dashboard, not in a 500."""
    _, outbox, _, dispatcher = make(dry_run=False, failure=RuntimeError("SMTP said no"))
    result = dispatcher.dispatch(source="gmail", destination="a@b.c", body="x")
    assert result.delivered is False
    assert "SMTP said no" in result.error
    assert outbox.recent()[0].error == result.error


# -- idempotency --------------------------------------------------------------


def test_one_reply_per_message_ever():
    """The Gmail poll re-sees the same unread mail forever; this is the belt."""
    _, outbox, _, dispatcher = make()
    first = dispatcher.dispatch(
        source="gmail", destination="a@b.c", body="x", reply_to_message_id="gmail:m1"
    )
    repeat = outbox.already_replied("gmail:m1")
    assert repeat is not None
    assert repeat.id == first.id
    assert repeat.duplicate is True
    assert outbox.already_replied("gmail:other") is None


def test_a_failed_reply_stays_retryable():
    """The message still has no answer, so it must not count as answered."""
    _, outbox, _, dispatcher = make(dry_run=False, failure=RuntimeError("nope"))
    dispatcher.dispatch(
        source="gmail", destination="a@b.c", body="x", reply_to_message_id="gmail:m1"
    )
    assert outbox.already_replied("gmail:m1") is None


def test_a_dry_run_reply_still_counts_as_answered():
    """During a rehearsal each message should be answered once, as it would be."""
    _, outbox, _, dispatcher = make(dry_run=True)
    dispatcher.dispatch(
        source="gmail", destination="a@b.c", body="x", reply_to_message_id="gmail:m1"
    )
    assert outbox.already_replied("gmail:m1") is not None


def test_a_fresh_send_is_not_deduplicated():
    """Nothing is being answered, so two identical announcements are two sends."""
    _, outbox, _, dispatcher = make()
    dispatcher.dispatch(source="gmail", destination="a@b.c", body="x")
    dispatcher.dispatch(source="gmail", destination="a@b.c", body="x")
    assert len(outbox.recent()) == 2


# -- gmail message building ---------------------------------------------------


@pytest.mark.parametrize(
    "original,expected",
    [
        ("Demo checklist", "Re: Demo checklist"),
        ("Re: Demo checklist", "Re: Demo checklist"),
        ("RE: shouting", "RE: shouting"),
        ("", "Re:"),
        (None, "Re:"),
    ],
)
def test_reply_subject_does_not_stack(original, expected):
    assert build_reply_subject(original) == expected


def test_references_appends_the_parent_without_duplicating_it():
    assert build_references("<a@x> <b@x>", "<c@x>") == "<a@x> <b@x> <c@x>"
    assert build_references(None, "<c@x>") == "<c@x>"
    assert build_references("<c@x>", "<c@x>") == "<c@x>"
    assert build_references(None, None) == ""


def test_a_reply_carries_the_headers_that_make_it_thread():
    message = build_email(
        from_address="me@gmail.com",
        from_name="Cluck In",
        to=["alice@example.com"],
        subject="Re: Demo",
        body="I am free on Tuesday.",
        in_reply_to="<abc@mail>",
        references="<root@mail> <abc@mail>",
    )
    assert message["From"] == "Cluck In <me@gmail.com>"
    assert message["In-Reply-To"] == "<abc@mail>"
    assert message["References"] == "<root@mail> <abc@mail>"
    # Its own Message-ID, so a reply to the reply threads too.
    assert message["Message-ID"].startswith("<")


def test_a_chinese_body_survives_the_wire():
    raw = build_email(
        from_address="me@gmail.com",
        from_name="",
        to=["a@b.c"],
        subject="測試",
        body="我週四下午有空。",
    ).as_string()
    assert "utf-8" in raw.lower()
    # The body must not appear as raw bytes reinterpreted as latin-1, which is
    # how Chinese silently turns into mojibake in a hand-rolled MIME message.
    assert "我週四" not in raw or "base64" in raw


def test_a_bare_from_address_has_no_display_name():
    message = build_email(
        from_address="me@gmail.com", from_name="", to=["a@b.c"], subject="x", body="y"
    )
    assert message["From"] == "me@gmail.com"


def test_the_sent_folder_is_found_by_its_flag_not_its_name():
    """The name is localized per account language; the flag is not."""

    class FakeConn:
        def list(self):
            return "OK", [
                b'(\\HasNoChildren) "/" "INBOX"',
                b'(\\HasNoChildren \\Sent) "/" "[Gmail]/\xe5\xaf\x84\xe4\xbb\xb6\xe5\xa4\x87\xe4\xbb\xbd"',
            ]

    found = GmailSender._discover_sent_mailbox(FakeConn())
    assert found is not None
    assert "INBOX" not in found


def test_no_sent_folder_is_none_not_a_guess():
    class FakeConn:
        def list(self):
            return "OK", [b'(\\HasNoChildren) "/" "INBOX"']

    assert GmailSender._discover_sent_mailbox(FakeConn()) is None


# -- slack --------------------------------------------------------------------


@pytest.mark.parametrize(
    "given,expected",
    [
        ("\U0001F44D", "thumbsup"),
        ("✅", "white_check_mark"),
        ("❤️", "heart"),
        (":thumbsup:", "thumbsup"),
        ("thumbsup", "thumbsup"),
        ("", "thumbsup"),
        ("some_custom_emoji", "some_custom_emoji"),
    ],
)
def test_reactions_take_a_short_name_not_a_character(given, expected):
    """reactions.add answers invalid_name for the character itself."""
    assert emoji_to_name(given) == expected


def test_every_address_in_the_payload_is_checked():
    _, _, sender, dispatcher = make(dry_run=False, allowlist="ok@example.com")
    with pytest.raises(SendBlocked):
        dispatcher.dispatch(
            source="gmail",
            destination="ok@example.com",
            body="x",
            also_check=["sneaky@example.com"],
        )
    assert sender.calls == []


def test_an_email_reaches_every_recipient_not_only_the_first():
    """The request takes a list; dropping the rest delivers to fewer people
    than the caller asked for, silently."""
    message = build_email(
        from_address="me@gmail.com",
        from_name="",
        to=["alice@example.com", "bob@example.com"],
        subject="x",
        body="y",
        cc=["carol@example.com"],
    )
    assert message["To"] == "alice@example.com, bob@example.com"
    assert message["Cc"] == "carol@example.com"
