import pytest
from fastapi.testclient import TestClient

import app as app_module
from buffer import MessageBuffer
from config import ConfigError, Settings


@pytest.fixture
def client(monkeypatch):
    """A fresh app with only the fixture adapter, independent of any local .env."""
    settings = Settings(
        _env_file=None,
        external_adapters="fixture",
        external_debug=True,
        external_buffer_size=50,
    )
    monkeypatch.setattr(app_module, "settings", settings)
    monkeypatch.setattr(app_module, "buffer", MessageBuffer(maxlen=50))
    monkeypatch.setattr(app_module, "adapters", [])
    with TestClient(app_module.app) as c:
        yield c


def test_health_reports_the_adapter(client):
    body = client.get("/health").json()
    assert body["status"] == "ok"
    assert [a["name"] for a in body["adapters"]] == ["fixture"]
    assert body["adapters"][0]["emitted"] == 2
    assert body["buffered"] == 2


def test_messages_returns_contract_valid_items(client, message_validator):
    body = client.get("/messages").json()
    assert body["count"] == 2
    assert body["hasMore"] is False
    assert body["replayed"] is False
    for message in body["messages"]:
        message_validator.validate(message)


def test_messages_carry_no_helper_fields(client):
    """additionalProperties:false -- the sequence lives in the cursor, not here."""
    allowed = {"id", "source", "sender", "title", "content", "timestamp", "unread", "metadata"}
    for message in client.get("/messages").json()["messages"]:
        assert set(message) <= allowed


def test_since_filters_by_instant_not_by_string(client):
    """The fixtures are 11:31+08:00 and 11:32+08:00, i.e. 03:31Z and 03:32Z.

    A string comparison keeps both, because "T11:3..." sorts after "T03:3...".
    """
    body = client.get("/messages", params={"since": "2026-09-19T03:31:30Z"}).json()
    assert [m["id"] for m in body["messages"]] == ["slack:msg-001"]


@pytest.mark.parametrize("bad", ["2026-09-19T11:31:00", "yesterday"])
def test_unusable_since_returns_422(client, bad):
    """Naive or malformed, never silently ignored -- the doc promises RFC 3339."""
    assert client.get("/messages", params={"since": bad}).status_code == 422


def test_cursor_drains_then_returns_empty(client):
    first = client.get("/messages").json()
    second = client.get("/messages", params={"cursor": first["cursor"]}).json()
    assert second["count"] == 0
    assert second["messages"] == []


def test_same_cursor_twice_is_idempotent(client):
    a = client.get("/messages").json()
    b = client.get("/messages").json()
    assert a["messages"] == b["messages"]
    assert a["cursor"] == b["cursor"]


def test_limit_sets_has_more(client):
    body = client.get("/messages", params={"limit": 1}).json()
    assert body["count"] == 1 and body["hasMore"] is True


def test_stale_epoch_replays_instead_of_returning_empty_forever(client):
    """A cursor from a previous process must not silently starve the client."""
    body = client.get("/messages", params={"cursor": "v1:deadbeef:999"}).json()
    assert body["replayed"] is True
    assert body["count"] == 2


def test_unparseable_cursor_replays(client):
    body = client.get("/messages", params={"cursor": "2026-09-19T11:31:00+08:00"}).json()
    assert body["replayed"] is True
    assert body["count"] == 2


def test_no_cursor_is_not_flagged_as_replay(client):
    assert client.get("/messages").json()["replayed"] is False


def test_limit_is_bounded(client):
    assert client.get("/messages", params={"limit": 0}).status_code == 422
    assert client.get("/messages", params={"limit": 501}).status_code == 422


def test_debug_inject_accepts_and_dedups(client):
    payload = {
        "id": "slack:C1:1.0", "source": "slack", "sender": "Bob", "title": None,
        "content": "hi", "timestamp": "2026-09-19T11:32:00+08:00", "unread": True,
    }
    assert client.post("/debug/inject", json=payload).json()["count"] == 1
    assert client.post("/debug/inject", json=payload).json()["count"] == 0


def test_debug_inject_rejects_extra_fields(client):
    payload = {
        "id": "slack:C1:2.0", "source": "slack", "sender": "Bob", "title": None,
        "content": "hi", "timestamp": "2026-09-19T11:32:00+08:00", "unread": True,
        "seq": 12,
    }
    assert client.post("/debug/inject", json=payload).status_code == 422


def test_debug_inject_hidden_when_disabled(monkeypatch):
    settings = Settings(_env_file=None, external_adapters="fixture", external_debug=False)
    monkeypatch.setattr(app_module, "settings", settings)
    monkeypatch.setattr(app_module, "buffer", MessageBuffer(maxlen=50))
    monkeypatch.setattr(app_module, "adapters", [])
    with TestClient(app_module.app) as c:
        assert c.post("/debug/inject", json={
            "id": "x:1", "source": "gmail", "sender": "A", "title": None,
            "content": "", "timestamp": "2026-09-19T11:31:00+08:00", "unread": True,
        }).status_code == 404


def test_unknown_adapter_name_refuses_to_start(monkeypatch):
    """Changed behaviour: this used to warn and carry on.

    Carrying on is what makes a typo'd EXTERNAL_ADAPTERS look like a working
    service with nothing to say -- and with 'fixture' still in the list, like a
    working service whose only messages are Bob and Alice. See
    tests/test_source_selection.py.
    """
    settings = Settings(_env_file=None, external_adapters="fixture,nonsense")
    monkeypatch.setattr(app_module, "settings", settings)
    monkeypatch.setattr(app_module, "buffer", MessageBuffer(maxlen=50))
    monkeypatch.setattr(app_module, "adapters", [])
    with pytest.raises(ConfigError, match="nonsense"):
        with TestClient(app_module.app):
            pass
