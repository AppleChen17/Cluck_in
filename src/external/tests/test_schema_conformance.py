"""Every message we produce is validated against the real schema file.

This is the only test that catches title="", sender="", metadata=null, a naive
timestamp, or a stray extra key before the C# deserializer does.
"""

import pytest

from schemas import ExternalMessage


def test_gmail_fixture_roundtrips_byte_for_byte(load_fixture):
    raw = load_fixture("external-message.gmail.json")
    assert ExternalMessage.model_validate(raw).model_dump() == raw


def test_slack_fixture_roundtrips_with_explicit_null_title(load_fixture):
    raw = load_fixture("external-message.slack.json")
    assert raw["title"] is None
    dumped = ExternalMessage.model_validate(raw).model_dump()
    # title must survive as an explicit null, unlike metadata which is dropped.
    assert "title" in dumped and dumped["title"] is None
    assert dumped == raw


def test_committed_fixtures_validate(load_fixture, message_validator):
    for name in ("external-message.gmail.json", "external-message.slack.json"):
        message_validator.validate(load_fixture(name))


def test_absent_metadata_is_omitted_not_null(message_validator):
    msg = ExternalMessage(
        id="gmail:x", source="gmail", sender="A", title=None,
        content="", timestamp="2026-09-19T11:31:00+08:00", unread=True,
    )
    wire = msg.model_dump()
    # metadata is "type": "object" and does not accept null.
    assert "metadata" not in wire
    message_validator.validate(wire)


def test_present_metadata_is_kept(message_validator):
    msg = ExternalMessage(
        id="slack:C1:1.0", source="slack", sender="Bob", title=None,
        content="hi", timestamp="2026-09-19T11:32:00+08:00", unread=True,
        metadata={"slackChannel": "C1"},
    )
    wire = msg.model_dump()
    assert wire["metadata"] == {"slackChannel": "C1"}
    message_validator.validate(wire)


def test_naive_timestamp_is_rejected_by_the_schema(message_validator):
    """Guards the format checker itself: without rfc3339-validator this passes."""
    bad = {
        "id": "gmail:x", "source": "gmail", "sender": "A", "title": None,
        "content": "", "timestamp": "2026-09-19T11:31:00", "unread": True,
    }
    with pytest.raises(Exception):
        message_validator.validate(bad)


@pytest.mark.parametrize("bad_field", ["sender", "id"])
def test_empty_required_labels_are_rejected(bad_field):
    payload = {
        "id": "gmail:x", "source": "gmail", "sender": "A", "title": None,
        "content": "", "timestamp": "2026-09-19T11:31:00+08:00", "unread": True,
    }
    payload[bad_field] = ""
    with pytest.raises(Exception):
        ExternalMessage.model_validate(payload)


def test_empty_title_is_rejected_by_the_model():
    """Blank subjects must become None upstream, never "" ."""
    with pytest.raises(Exception):
        ExternalMessage(
            id="gmail:x", source="gmail", sender="A", title="",
            content="", timestamp="2026-09-19T11:31:00+08:00", unread=True,
        )


def test_unknown_source_is_rejected():
    """The shared enum is gmail|slack only -- ai-engine's extra "other" is wrong."""
    with pytest.raises(Exception):
        ExternalMessage(
            id="x:1", source="other", sender="A", title=None,
            content="", timestamp="2026-09-19T11:31:00+08:00", unread=True,
        )


def test_extra_fields_are_rejected():
    """additionalProperties:false -- a stray helper field breaks strict consumers."""
    with pytest.raises(Exception):
        ExternalMessage(
            id="x:1", source="gmail", sender="A", title=None, content="",
            timestamp="2026-09-19T11:31:00+08:00", unread=True, seq=12,
        )
