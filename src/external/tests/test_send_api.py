"""The outbound and calendar endpoints, through the real app.

Dry run throughout, which is both the default and the point: every assertion
here holds without a Gmail account, a Slack workspace or a Google project.
"""

import pytest
from fastapi.testclient import TestClient

import app as app_module
from buffer import MessageBuffer
from config import Settings
from idempotency import OnceByKey
from reply_registry import ReplyRegistry, ReplyTarget
from sender.outbox import Outbox

GMAIL_ID = "gmail:abc123@mail.example.com"
SLACK_ID = "slack:C08ABCDEF:1789788720.000200"


def build_client(monkeypatch, **overrides) -> TestClient:
    settings = Settings(
        _env_file=None,
        external_adapters="fixture",
        external_debug=True,
        external_buffer_size=50,
        **overrides,
    )
    monkeypatch.setattr(app_module, "settings", settings)
    monkeypatch.setattr(app_module, "buffer", MessageBuffer(maxlen=50))
    monkeypatch.setattr(app_module, "replies", ReplyRegistry(maxlen=50))
    monkeypatch.setattr(app_module, "outbox", Outbox(maxlen=50))
    monkeypatch.setattr(app_module, "created_events", OnceByKey(maxlen=50))
    monkeypatch.setattr(app_module, "adapters", [])
    return TestClient(app_module.app)


@pytest.fixture
def client(monkeypatch):
    with build_client(monkeypatch) as c:
        # Two routes the fixture adapter cannot supply: a real Gmail address
        # only exists on a real message, and the committed Slack fixture id has
        # no channel in it.
        app_module.replies.remember(
            ReplyTarget(
                message_id=GMAIL_ID,
                source="gmail",
                address="alice@example.com",
                rfc_message_id="<abc123@mail.example.com>",
                references="<root@mail.example.com>",
                subject="Demo checklist",
            )
        )
        app_module.replies.remember(
            ReplyTarget(
                message_id=SLACK_ID,
                source="slack",
                channel="C08ABCDEF",
                ts="1789788720.000200",
                thread_ts="1789788720.000200",
            )
        )
        yield c


# -- health -------------------------------------------------------------------


def test_health_reports_the_outbound_half(client):
    body = client.get("/health").json()
    assert body["sending"]["dryRun"] is True
    assert body["sending"]["replyTargets"] == 2
    assert body["calendar"]["backend"] == "memory"


def test_health_still_reports_the_inbound_half(client):
    """The new fields must not have displaced the old ones."""
    body = client.get("/health").json()
    assert body["status"] == "ok"
    assert [a["name"] for a in body["adapters"]] == ["fixture"]


# -- replying -----------------------------------------------------------------


def test_replying_to_a_gmail_message_routes_by_id(client):
    body = client.post("/reply", json={"messageId": GMAIL_ID, "body": "Tuesday works."}).json()
    assert body["source"] == "gmail"
    assert body["target"] == "alice@example.com"
    assert body["dryRun"] is True
    assert body["delivered"] is False
    assert body["inReplyTo"] == GMAIL_ID
    assert "Tuesday works." in body["preview"]


def test_replying_to_a_slack_message_routes_by_id(client):
    body = client.post("/reply", json={"messageId": SLACK_ID, "body": "on it"}).json()
    assert body["source"] == "slack"
    assert body["target"] == "C08ABCDEF"


def test_a_second_reply_to_the_same_message_sends_nothing(client):
    """One incoming mail must not become a reply every poll, forever."""
    first = client.post("/reply", json={"messageId": GMAIL_ID, "body": "first"}).json()
    second = client.post("/reply", json={"messageId": GMAIL_ID, "body": "second"}).json()
    assert second["duplicate"] is True
    assert second["id"] == first["id"]
    assert "second" not in second["preview"]
    assert client.get("/outbox").json()["count"] == 1


def test_replying_to_an_unknown_message_is_a_404_that_explains_itself(client):
    body = client.post("/reply", json={"messageId": "gmail:never-seen", "body": "x"})
    assert body.status_code == 404
    detail = body.json()["detail"]
    assert "/send/gmail" in detail


def test_an_empty_reply_body_is_rejected(client):
    assert client.post("/reply", json={"messageId": GMAIL_ID, "body": ""}).status_code == 422


def test_an_unknown_field_is_rejected(client):
    """extra=forbid: a typo must fail loudly, not be silently dropped."""
    response = client.post(
        "/reply", json={"messageId": GMAIL_ID, "body": "x", "subjekt": "typo"}
    )
    assert response.status_code == 422


# -- fresh sends --------------------------------------------------------------


def test_sending_a_fresh_email(client):
    body = client.post(
        "/send/gmail",
        json={"to": ["bob@example.com"], "subject": "Hi", "body": "Hello"},
    ).json()
    assert body["target"] == "bob@example.com"
    assert body["inReplyTo"] is None


def test_sending_to_slack_needs_a_channel_from_somewhere(client):
    response = client.post("/send/slack", json={"text": "hello"})
    assert response.status_code == 422
    assert "SLACK_DEFAULT_CHANNEL" in response.json()["detail"]


def test_the_default_slack_channel_is_used_when_none_is_given(monkeypatch):
    with build_client(monkeypatch, slack_default_channel="C_DEFAULT") as client:
        body = client.post("/send/slack", json={"text": "hello"}).json()
        assert body["target"] == "C_DEFAULT"


def test_an_email_with_no_recipient_is_rejected(client):
    response = client.post("/send/gmail", json={"to": [], "subject": "x", "body": "y"})
    assert response.status_code == 422


# -- the allowlist, through HTTP ----------------------------------------------


def test_a_live_send_outside_the_allowlist_is_a_403(monkeypatch):
    with build_client(
        monkeypatch, external_send_dry_run=False, external_send_allowlist="boss@example.com"
    ) as client:
        response = client.post(
            "/send/gmail", json={"to": ["alice@example.com"], "subject": "x", "body": "y"}
        )
        assert response.status_code == 403
        assert "EXTERNAL_SEND_ALLOWLIST" in response.json()["detail"]


def test_a_live_send_with_no_credentials_is_a_503_not_a_crash(monkeypatch):
    with build_client(monkeypatch, external_send_dry_run=False) as client:
        response = client.post(
            "/send/gmail", json={"to": ["alice@example.com"], "subject": "x", "body": "y"}
        )
        assert response.status_code == 503
        assert ".env" in response.json()["detail"]


# -- reactions ----------------------------------------------------------------


def test_reacting_to_a_slack_message(client):
    body = client.post("/react", json={"messageId": SLACK_ID, "emoji": "\U0001F44D"}).json()
    assert body["dryRun"] is True
    assert body["delivered"] is False


def test_reacting_to_an_email_says_why_not(client):
    response = client.post("/react", json={"messageId": GMAIL_ID, "emoji": "\U0001F44D"})
    assert response.status_code == 422
    assert "Slack" in response.json()["detail"]


def test_reacting_to_an_unknown_message_is_a_404(client):
    assert client.post("/react", json={"messageId": "slack:nope"}).status_code == 404


# -- the outbox ---------------------------------------------------------------


def test_the_outbox_lists_dry_runs_newest_first(client):
    client.post("/send/gmail", json={"to": ["a@x.com"], "subject": "1", "body": "first"})
    client.post("/send/gmail", json={"to": ["b@x.com"], "subject": "2", "body": "second"})
    body = client.get("/outbox").json()
    assert body["count"] == 2
    assert body["sent"][0]["target"] == "b@x.com"


def test_the_outbox_starts_empty(client):
    assert client.get("/outbox").json() == {"sent": [], "count": 0}


# -- injected messages can be answered ----------------------------------------


def test_an_injected_slack_message_becomes_replyable(client):
    """The credential-free demo path: inject, then reply to what you injected."""
    message = {
        "id": "slack:C_DEMO:1789790000.000100",
        "source": "slack",
        "sender": "Bob",
        "title": None,
        "content": "你什麼時候有空？",
        "timestamp": "2026-09-19T11:32:00+08:00",
        "unread": True,
        "metadata": {"slackChannel": "C_DEMO"},
    }
    assert client.post("/debug/inject", json=message).status_code == 200
    body = client.post("/reply", json={"messageId": message["id"], "body": "有空"}).json()
    assert body["target"] == "C_DEMO"


def test_an_injected_email_needs_an_address_to_be_replyable(client):
    """sender is a display name by contract, so it cannot supply a route."""
    message = {
        "id": "gmail:injected@example.com",
        "source": "gmail",
        "sender": "Alice Chen",
        "title": "Hi",
        "content": "when are you free?",
        "timestamp": "2026-09-19T11:31:00+08:00",
        "unread": True,
    }
    client.post("/debug/inject", json=message)
    assert client.post("/reply", json={"messageId": message["id"], "body": "x"}).status_code == 404

    with_address = dict(message, id="gmail:injected2@example.com",
                        metadata={"replyToAddress": "alice@example.com"})
    client.post("/debug/inject", json=with_address)
    body = client.post("/reply", json={"messageId": with_address["id"], "body": "x"}).json()
    assert body["target"] == "alice@example.com"


# -- calendar -----------------------------------------------------------------


def test_availability_returns_slots_and_a_sentence(client):
    body = client.post("/calendar/availability", json={"durationMinutes": 30}).json()
    assert body["backend"] == "memory"
    assert body["durationMinutes"] == 30
    assert body["text"]
    for slot in body["slots"]:
        assert slot["start"] < slot["end"]


def test_availability_never_proposes_a_slot_before_the_lead_time(monkeypatch):
    """Offering a meeting eight minutes from now reads as a bug."""
    with build_client(monkeypatch, calendar_lead_minutes=600) as client:
        body = client.post("/calendar/availability", json={}).json()
        for slot in body["slots"]:
            assert slot["start"] >= body["searchedFrom"]


def test_availability_rejects_an_absurd_duration(client):
    assert client.post("/calendar/availability", json={"durationMinutes": 5000}).status_code == 422


def _tomorrow_afternoon() -> tuple[str, str]:
    """Relative to now, never a hardcoded date: a fixed date drifts into the
    past and the test starts failing on a day nobody touched the code."""
    from datetime import datetime, timedelta

    start = (datetime.now().astimezone() + timedelta(days=1)).replace(
        hour=14, minute=0, second=0, microsecond=0
    )
    return start.isoformat(), (start + timedelta(hours=1)).isoformat()


def test_a_created_event_shows_up_in_the_listing(client):
    start, end = _tomorrow_afternoon()
    created = client.post(
        "/calendar/events",
        json={
            "title": "Cluck In demo",
            "startTime": start,
            "endTime": end,
            "fromMessageId": SLACK_ID,
        },
    )
    assert created.status_code == 201
    event = created.json()["events"][0]
    # Recorded so the dashboard can say which message caused this, and never
    # sent to Google.
    assert event["metadata"]["fromMessageId"] == SLACK_ID

    listed = client.get("/calendar/events", params={"withinDays": 7}).json()
    assert [e["id"] for e in listed["events"]] == [event["id"]]


def test_an_event_outside_the_window_is_not_listed(client):
    client.post(
        "/calendar/events",
        json={
            "title": "Far future",
            "startTime": "2099-01-05T14:00:00+08:00",
            "endTime": "2099-01-05T15:00:00+08:00",
        },
    )
    assert client.get("/calendar/events", params={"withinDays": 60}).json()["count"] == 0


def test_a_created_event_blocks_the_slot_it_occupies(client):
    """The loop that matters: accept a meeting, stop offering that time."""
    before = client.post("/calendar/availability", json={"durationMinutes": 30, "limit": 20}).json()
    if not before["slots"]:
        pytest.skip("no free slots in the window right now")
    taken = before["slots"][0]
    client.post(
        "/calendar/events",
        json={"title": "Taken", "startTime": taken["start"], "endTime": taken["end"]},
    )
    after = client.post("/calendar/availability", json={"durationMinutes": 30, "limit": 20}).json()
    assert taken not in after["slots"]


@pytest.mark.parametrize(
    "start,end",
    [
        ("2026-09-20T14:00:00", "2026-09-20T15:00:00"),  # no offset
        ("2026-09-20T15:00:00+08:00", "2026-09-20T14:00:00+08:00"),  # backwards
        ("2026-09-20T15:00:00+08:00", "2026-09-20T15:00:00+08:00"),  # zero length
        ("tomorrow afternoon", "2026-09-20T15:00:00+08:00"),
    ],
)
def test_unusable_event_times_are_rejected_here_not_at_the_provider(client, start, end):
    response = client.post(
        "/calendar/events", json={"title": "x", "startTime": start, "endTime": end}
    )
    assert response.status_code == 422


def test_listed_events_validate_against_the_contract(client, validator_for):
    start, end = _tomorrow_afternoon()
    client.post(
        "/calendar/events",
        json={
            "title": "Contract check",
            "startTime": start,
            "endTime": end,
            "attendees": ["alice@example.com"],
        },
    )
    body = client.get("/calendar/events", params={"withinDays": 7}).json()
    assert body["count"] == 1
    validator = validator_for("external-event.schema.json")
    for event in body["events"]:
        validator.validate(event)


def test_one_calendar_entry_per_message(client):
    """A replayed cursor must not put the same meeting on the calendar twice."""
    start, end = _tomorrow_afternoon()
    payload = {
        "title": "Sprint review",
        "startTime": start,
        "endTime": end,
        "fromMessageId": SLACK_ID,
    }
    first = client.post("/calendar/events", json=payload).json()
    again = client.post("/calendar/events", json=dict(payload, title="Renamed")).json()
    assert again["duplicate"] is True
    assert again["events"][0]["id"] == first["events"][0]["id"]
    assert again["events"][0]["title"] == "Sprint review"
    assert client.get("/calendar/events", params={"withinDays": 7}).json()["count"] == 1


def test_an_event_with_no_source_message_is_never_deduplicated(client):
    """Two meetings that answer nothing are two meetings, not a mistake."""
    start, end = _tomorrow_afternoon()
    client.post("/calendar/events", json={"title": "A", "startTime": start, "endTime": end})
    second = client.post(
        "/calendar/events", json={"title": "B", "startTime": start, "endTime": end}
    ).json()
    assert second["duplicate"] is False
    assert client.get("/calendar/events", params={"withinDays": 7}).json()["count"] == 2


def test_every_recipient_is_allowlisted_not_just_the_first(monkeypatch):
    """An allowlist that only checks to[0] can be walked straight past."""
    with build_client(
        monkeypatch, external_send_dry_run=False, external_send_allowlist="ok@example.com"
    ) as client:
        response = client.post(
            "/send/gmail",
            json={
                "to": ["ok@example.com", "anyone@elsewhere.com"],
                "subject": "x",
                "body": "y",
            },
        )
        assert response.status_code == 403
        assert "anyone@elsewhere.com" in response.json()["detail"]


def test_cc_is_allowlisted_too(monkeypatch):
    with build_client(
        monkeypatch, external_send_dry_run=False, external_send_allowlist="ok@example.com"
    ) as client:
        response = client.post(
            "/send/gmail",
            json={
                "to": ["ok@example.com"],
                "cc": ["anyone@elsewhere.com"],
                "subject": "x",
                "body": "y",
            },
        )
        assert response.status_code == 403
