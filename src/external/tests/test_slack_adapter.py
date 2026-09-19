"""Slack normalization and event filtering, driven by canned event payloads.

No workspace, no token, no network.
"""

import pytest

from adapters.slack_adapter import (
    SlackAdapter,
    build_message,
    should_ignore,
    slack_ts_format,
    slack_ts_to_rfc3339,
)

TS = "1789788720.000200"  # 2026-09-19T11:32:00+08:00, matching the committed fixtures


def event(**overrides) -> dict:
    base = {
        "type": "message",
        "channel": "C08ABCDEF",
        "user": "U0123ABCD",
        "text": "Production build failed for the Cluck In prototype.",
        "ts": TS,
    }
    base.update(overrides)
    return base


# -- filtering ----------------------------------------------------------------


def test_plain_human_message_is_kept():
    assert should_ignore(event()) is False


def test_bot_messages_are_dropped():
    """Otherwise our own notifications could feed back in."""
    assert should_ignore(event(bot_id="B123")) is True


@pytest.mark.parametrize(
    "subtype",
    ["bot_message", "message_changed", "message_deleted", "channel_join", "thread_broadcast"],
)
def test_non_message_subtypes_are_dropped(subtype):
    assert should_ignore(event(subtype=subtype)) is True


def test_non_message_event_types_are_dropped():
    assert should_ignore(event(type="reaction_added")) is True


def test_events_without_channel_or_ts_are_dropped():
    assert should_ignore(event(channel=None)) is True
    assert should_ignore(event(ts=None)) is True


def test_file_only_message_is_kept_with_empty_content():
    """The schema allows empty content; the app decides what to do with it."""
    evt = event(text="", files=[{"id": "F1"}])
    assert should_ignore(evt) is False
    assert build_message(evt, sender="Bob").content == ""


# -- normalization ------------------------------------------------------------


def test_id_pairs_channel_and_ts():
    # ts alone is not unique across channels.
    assert build_message(event(), sender="Bob").id == "slack:C08ABCDEF:" + TS


def test_timestamp_is_rfc3339_with_an_offset():
    stamp = slack_ts_to_rfc3339(TS, tz_mode="utc")
    assert stamp == "2026-09-19T03:32:00+00:00"


def test_title_is_always_null_for_slack():
    assert build_message(event(), sender="Bob").title is None


def test_unresolved_user_still_yields_a_non_empty_sender():
    """sender has minLength 1 -- an empty label fails the contract."""
    assert build_message(event(), sender="").sender == "Slack user"


def test_markup_is_unwrapped():
    evt = event(text="see <https://ci.example/build/7|build 7> from <@U999> in <#C1|ci>")
    assert build_message(evt, sender="Bob").content == "see build 7 from @U999 in #ci"


def test_mentions_resolve_when_a_resolver_is_supplied():
    evt = event(text="ping <@U999>")
    msg = build_message(evt, sender="Bob", resolve_user=lambda uid: "Alice")
    assert msg.content == "ping @Alice"


def test_channel_goes_into_metadata():
    assert build_message(event(), sender="Bob").metadata["slackChannel"] == "C08ABCDEF"


def test_thread_reply_records_a_non_contractual_thread_ts():
    """No threadId exists in any contract, so it can only live in metadata."""
    evt = event(thread_ts="1789788700.000100")
    assert build_message(evt, sender="Bob").metadata["slackThreadTs"] == "1789788700.000100"


def test_root_message_has_no_thread_ts():
    evt = event(thread_ts=TS)
    assert "slackThreadTs" not in build_message(evt, sender="Bob").metadata


def test_content_is_truncated():
    msg = build_message(event(text="x" * 5000), sender="Bob", max_chars=50)
    assert len(msg.content) == 51


def test_produced_message_validates_against_the_schema(message_validator):
    message_validator.validate(build_message(event(), sender="Bob").model_dump())


def test_metadata_survives_serialization(message_validator):
    wire = build_message(event(), sender="Bob").model_dump()
    assert wire["metadata"] == {"slackChannel": "C08ABCDEF"}
    message_validator.validate(wire)


# -- backfill window ----------------------------------------------------------
#
# Slack's ts is <seconds>.<exactly 6 digits>. Hand it a float repr with 7
# fractional digits and it shifts the extra one into the seconds, producing a
# far-future window that matches nothing -- with ok=true and no error at all.


def test_the_float_repr_that_broke_the_backfill():
    """Regression: str() of this exact value was echoed back as 17897936046."""
    epoch = 1789793604.6410232
    assert str(epoch) == "1789793604.6410232"  # 7 fractional digits
    assert slack_ts_format(epoch) == "1789793604.641023"


@pytest.mark.parametrize(
    "epoch",
    [1789793604.6410232, 1789793604.0, 1789793604.1, 0.0, 1789811399.717649],
)
def test_ts_always_has_exactly_six_fractional_digits(epoch):
    seconds, _, fraction = slack_ts_format(epoch).partition(".")
    assert seconds.isdigit() and len(fraction) == 6


@pytest.mark.parametrize("epoch", [1789793604.6410232, 1789811399.717649])
def test_ts_round_trips_to_the_same_instant(epoch):
    assert abs(float(slack_ts_format(epoch)) - epoch) < 1e-6


class _FakeWeb:
    """Records what the backfill actually sends, with no network."""

    def __init__(self):
        self.history_kwargs = []

    def users_conversations(self, **_):
        return {"channels": [{"id": "C1"}]}

    def conversations_history(self, **kwargs):
        self.history_kwargs.append(kwargs)
        return {"messages": []}


def test_backfill_sends_a_well_formed_oldest(monkeypatch):
    """Guards the wiring, not just the helper: str(oldest) here was the bug."""
    from config import Settings

    adapter = SlackAdapter(Settings(_env_file=None, slack_backfill_minutes=300))
    fake = _FakeWeb()
    adapter._web = fake
    adapter._sink = lambda _m: True
    adapter._backfill()

    assert len(fake.history_kwargs) == 1
    oldest = fake.history_kwargs[0]["oldest"]
    seconds, _, fraction = oldest.partition(".")
    assert len(fraction) == 6, f"Slack would misparse {oldest!r}"
    # 300 minutes back, within a generous tolerance for test runtime
    import time
    assert 0 < time.time() - float(oldest) < 300 * 60 + 60
