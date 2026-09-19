"""Which message source runs, and what happens when it cannot.

The failure this file exists to prevent: EXTERNAL_ADAPTERS=slack with a missing
or misspelled token, and GET /messages answering 200 with the committed
Bob/Alice fixtures — which looks exactly like a working Slack integration that
happens to have quiet channels.

Every Settings here passes `_env_file=None`, so a filled-in local
src/external/.env cannot make these pass or fail.

No workspace, no token, no network.
"""

import logging

import pytest
from fastapi.testclient import TestClient

import app as app_module
from buffer import MessageBuffer
from config import ConfigError, Settings

BOT = "xoxb-0000000000-test"
APP = "xapp-1-A000000-test"


def cfg(**overrides) -> Settings:
    base = {
        "_env_file": None,
        "slack_bot_token": "",
        "slack_app_token": "",
        "gmail_address": "",
        "gmail_app_password": "",
    }
    base.update(overrides)
    return Settings(**base)


# -- selection is explicit ----------------------------------------------------


def test_fixture_needs_no_credentials():
    """The credential-free default keeps working, unchanged."""
    cfg(external_adapters="fixture").validate_sources()


def test_slack_with_both_tokens_is_accepted():
    cfg(external_adapters="slack", slack_bot_token=BOT, slack_app_token=APP).validate_sources()


def test_selecting_slack_without_tokens_is_a_config_error():
    """Requirement in one test: no silent fall back to Bob and Alice."""
    with pytest.raises(ConfigError) as exc:
        cfg(external_adapters="slack").validate_sources()
    message = str(exc.value)
    assert "SLACK_BOT_TOKEN" in message
    assert "SLACK_APP_TOKEN" in message
    # It has to say where to put them and how to get back to a working service.
    assert ".env" in message
    assert "EXTERNAL_ADAPTERS=fixture" in message


def test_copied_env_example_placeholders_count_as_unset():
    """`.env.example` ships the bare prefixes; copying it configures nothing."""
    with pytest.raises(ConfigError) as exc:
        cfg(external_adapters="slack", slack_bot_token="xoxb-", slack_app_token="xapp-")\
            .validate_sources()
    assert "SLACK_BOT_TOKEN" in str(exc.value)


def test_swapped_tokens_are_rejected_by_prefix():
    """Left alone this fails at connect as invalid_auth, naming no variable."""
    with pytest.raises(ConfigError) as exc:
        cfg(external_adapters="slack", slack_bot_token=APP, slack_app_token=BOT)\
            .validate_sources()
    assert "xoxb-" in str(exc.value)
    assert "xapp-" in str(exc.value)


def test_only_the_missing_token_is_reported():
    with pytest.raises(ConfigError) as exc:
        cfg(external_adapters="slack", slack_bot_token=BOT).validate_sources()
    message = str(exc.value)
    assert "SLACK_APP_TOKEN is not set" in message
    assert "SLACK_BOT_TOKEN is not set" not in message


def test_a_misspelled_source_is_rejected_not_skipped():
    """'slck' must not leave the service running with no source at all."""
    with pytest.raises(ConfigError) as exc:
        cfg(external_adapters="slck").validate_sources()
    assert "'slck'" in str(exc.value)
    assert "fixture, gmail, slack" in str(exc.value)


def test_empty_adapter_list_is_rejected():
    with pytest.raises(ConfigError):
        cfg(external_adapters="  ").validate_sources()


def test_gmail_is_validated_the_same_way():
    with pytest.raises(ConfigError) as exc:
        cfg(external_adapters="gmail").validate_sources()
    assert "GMAIL_ADDRESS" in str(exc.value)


def test_slack_tokens_are_not_required_when_slack_is_not_selected():
    """Credentials for a source you did not select are nobody's business."""
    cfg(external_adapters="fixture", gmail_address="", slack_bot_token="").validate_sources()


def test_names_are_case_and_space_insensitive():
    cfg(external_adapters=" Slack , FIXTURE ", slack_bot_token=BOT, slack_app_token=APP)\
        .validate_sources()


# -- what actually gets built -------------------------------------------------


def test_slack_selection_builds_the_slack_adapter():
    """Built, not started: no socket is opened here."""
    from adapters.slack_adapter import SlackAdapter

    built = app_module._build_adapters(
        cfg(external_adapters="slack", slack_bot_token=BOT, slack_app_token=APP)
    )
    assert [type(a) for a in built] == [SlackAdapter]
    assert built[0].name == "slack"


def test_fixture_selection_still_builds_the_fixture_adapter():
    from adapters.fixture_adapter import FixtureAdapter

    built = app_module._build_adapters(cfg(external_adapters="fixture"))
    assert [type(a) for a in built] == [FixtureAdapter]


# -- startup logging ----------------------------------------------------------


def test_startup_announces_the_slack_source(caplog):
    with caplog.at_level(logging.INFO, logger="external"):
        app_module._announce_source(
            cfg(external_adapters="slack", slack_bot_token=BOT, slack_app_token=APP)
        )
    assert "Message source: slack" in caplog.text


def test_startup_announces_the_fixture_source(caplog):
    with caplog.at_level(logging.INFO, logger="external"):
        app_module._announce_source(cfg(external_adapters="fixture"))
    assert "Message source: fixture" in caplog.text


def test_mixing_fixture_with_slack_warns(caplog):
    """Legitimate for a rehearsal, but never something to discover mid-demo."""
    with caplog.at_level(logging.INFO, logger="external"):
        app_module._announce_source(
            cfg(external_adapters="slack,fixture", slack_bot_token=BOT, slack_app_token=APP)
        )
    assert "Message source: slack, fixture" in caplog.text
    warnings = [r for r in caplog.records if r.levelno >= logging.WARNING]
    assert warnings and "Bob/Alice" in warnings[0].getMessage()


def test_slack_enabled_without_selecting_slack_warns(caplog):
    """SLACK_ENABLED is read by nothing; docs/Setup.md implies otherwise."""
    with caplog.at_level(logging.INFO, logger="external"):
        app_module._announce_source(cfg(external_adapters="fixture", slack_enabled=True))
    warnings = [r.getMessage() for r in caplog.records if r.levelno >= logging.WARNING]
    assert any("SLACK_ENABLED" in w and "ignored" in w for w in warnings)


# -- end to end through the app ----------------------------------------------


def _client(settings):
    return TestClient(app_module.app)


def test_misconfigured_slack_refuses_to_start(monkeypatch):
    """The whole point: startup fails instead of serving fixture messages."""
    monkeypatch.setattr(app_module, "settings", cfg(external_adapters="slack"))
    monkeypatch.setattr(app_module, "buffer", MessageBuffer(maxlen=50))
    monkeypatch.setattr(app_module, "adapters", [])
    with pytest.raises(ConfigError) as exc:
        with TestClient(app_module.app):
            pass
    assert "SLACK_BOT_TOKEN" in str(exc.value)


def test_misconfigured_slack_never_serves_fixture_messages(monkeypatch):
    """Same run, stated as the symptom a demo would show."""
    monkeypatch.setattr(app_module, "settings", cfg(external_adapters="slack"))
    monkeypatch.setattr(app_module, "buffer", MessageBuffer(maxlen=50))
    monkeypatch.setattr(app_module, "adapters", [])
    with pytest.raises(ConfigError):
        with TestClient(app_module.app):
            pass
    # Nothing reached the buffer, so no Bob and no Alice could be served.
    assert app_module.buffer.stats()["buffered"] == 0


def test_fixture_source_still_serves_the_committed_messages(monkeypatch):
    """The preserved fixture path, asserted from the HTTP surface."""
    monkeypatch.setattr(app_module, "settings", cfg(external_adapters="fixture"))
    monkeypatch.setattr(app_module, "buffer", MessageBuffer(maxlen=50))
    monkeypatch.setattr(app_module, "adapters", [])
    with TestClient(app_module.app) as client:
        body = client.get("/messages").json()
    assert body["count"] == 2
    assert {m["source"] for m in body["messages"]} == {"gmail", "slack"}


def test_slack_source_starts_and_reports_itself(monkeypatch):
    """A configured Slack source runs; the socket connect itself is stubbed.

    Proves the wiring: selecting slack puts a slack adapter in /health and puts
    no fixture message in the buffer.
    """
    from adapters.slack_adapter import SlackAdapter

    monkeypatch.setattr(SlackAdapter, "start", lambda self, sink: self._mark_connected(True))
    monkeypatch.setattr(SlackAdapter, "stop", lambda self: self._mark_connected(False))
    monkeypatch.setattr(
        app_module,
        "settings",
        cfg(external_adapters="slack", slack_bot_token=BOT, slack_app_token=APP),
    )
    monkeypatch.setattr(app_module, "buffer", MessageBuffer(maxlen=50))
    monkeypatch.setattr(app_module, "adapters", [])
    with TestClient(app_module.app) as client:
        health = client.get("/health").json()
        messages = client.get("/messages").json()
    assert [a["name"] for a in health["adapters"]] == ["slack"]
    assert health["adapters"][0]["connected"] is True
    assert messages["count"] == 0


def test_a_live_slack_failure_degrades_rather_than_refusing_to_start(monkeypatch, caplog):
    """A revoked token or a dead network is not a config error.

    The service keeps serving so the dashboard and the send endpoints stay up;
    GET /health carries the reason and the log says no source is connected.
    """
    from adapters.slack_adapter import SlackAdapter

    def explode(self, sink):
        raise ConnectionError("slack unreachable")

    monkeypatch.setattr(SlackAdapter, "start", explode)
    monkeypatch.setattr(SlackAdapter, "stop", lambda self: None)
    monkeypatch.setattr(
        app_module,
        "settings",
        cfg(external_adapters="slack", slack_bot_token=BOT, slack_app_token=APP),
    )
    monkeypatch.setattr(app_module, "buffer", MessageBuffer(maxlen=50))
    monkeypatch.setattr(app_module, "adapters", [])
    with caplog.at_level(logging.INFO, logger="external"):
        with TestClient(app_module.app) as client:
            health = client.get("/health").json()
    assert health["status"] == "degraded"
    assert "slack unreachable" in health["adapters"][0]["lastError"]
    assert "No message source is connected" in caplog.text
