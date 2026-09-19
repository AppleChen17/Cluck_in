"""Every committed schema is well formed, and every fixture matches its schema.

Not only this module's contracts. `docs/data-contracts.md` says to load all of
them into one registry, check their syntax, then validate each fixture with
date-time format checking on -- and nothing was doing that. A schema nobody
validates is a schema that drifts: a bad `$ref`, a typo in an enum, or a
fixture that stopped matching after a field was renamed all pass silently until
some consumer fails at runtime, in whichever language happens to hit it first.

This suite lives here because this module already has jsonschema as a test
dependency and a registry fixture. It asserts nothing about `src/external`
specifically and would belong anywhere the contracts are checked.
"""

import json

import pytest
from jsonschema import Draft202012Validator

from config import FIXTURES_DIR, SCHEMAS_DIR

SCHEMA_FILES = sorted(p.name for p in SCHEMAS_DIR.glob("*.schema.json"))

# Fixture file -> the schema it must satisfy. Mirrors the table at the bottom of
# docs/data-contracts.md; a fixture added without an entry here fails below
# rather than being quietly unchecked.
FIXTURE_SCHEMAS = {
    "action-command.notification.json": "action-command.schema.json",
    "ai-decision.urgent.json": "ai-decision.schema.json",
    "ai-request.message.json": "ai-request.schema.json",
    "app-state.focus.json": "app-state.schema.json",
    "external-event.calendar.json": "external-event.schema.json",
    "external-message.gmail.json": "external-message.schema.json",
    "external-message.slack.json": "external-message.schema.json",
    "input-event.start-focus.json": "input-event.schema.json",
    "intent-analyze-request.message.json": "intent-analyze-request.schema.json",
    "intent-decision.meeting.json": "intent-decision.schema.json",
    "intent-decision.no-time.json": "intent-decision.schema.json",
    "session-context.focus.json": "session-context.schema.json",
    "task-analyze-request.web.json": "task-analyze-request.schema.json",
    "task-decision.allow.json": "task-decision.schema.json",
}


@pytest.mark.parametrize("name", SCHEMA_FILES)
def test_every_schema_is_valid_draft_2020_12(name):
    Draft202012Validator.check_schema(
        json.loads((SCHEMAS_DIR / name).read_text(encoding="utf-8"))
    )


@pytest.mark.parametrize("name", SCHEMA_FILES)
def test_every_schema_declares_the_expected_id(name):
    """The registry is keyed by `$id`, and relative `$ref`s resolve against it.

    A mismatched or missing id makes a `$ref` reach for the network instead,
    which hangs rather than failing -- cluck-in.example is a reserved domain
    that answers nothing.
    """
    contents = json.loads((SCHEMAS_DIR / name).read_text(encoding="utf-8"))
    assert contents["$id"] == "https://cluck-in.example/schemas/v1/" + name


@pytest.mark.parametrize("fixture,schema", sorted(FIXTURE_SCHEMAS.items()))
def test_every_fixture_matches_its_schema(fixture, schema, validator_for, load_fixture):
    validator_for(schema).validate(load_fixture(fixture))


def test_every_fixture_is_accounted_for():
    """A new fixture must be listed above, so it cannot arrive unchecked."""
    on_disk = {p.name for p in FIXTURES_DIR.glob("*.json")}
    assert on_disk == set(FIXTURE_SCHEMAS), {
        "unlisted": sorted(on_disk - set(FIXTURE_SCHEMAS)),
        "missing": sorted(set(FIXTURE_SCHEMAS) - on_disk),
    }


# -- the intent contract, whose rules are easy to state and easy to break -----


@pytest.fixture
def intent_validator(validator_for):
    return validator_for("intent-decision.schema.json")


def _decision(**overrides) -> dict:
    base = {
        "messageId": "slack:C08ABCDEF:1789788720.000200",
        "intent": "meeting_invite",
        "confidence": 0.9,
        "reason": "訊息指定了會議時間。",
    }
    base.update(overrides)
    return base


def test_a_meeting_with_no_usable_time_is_valid(intent_validator):
    """The abstention case, and the whole point of allowing null.

    "I can see this is about a meeting but I cannot tell when" must be
    expressible. Forcing a time here is how a model ends up inventing one.
    """
    intent_validator.validate(_decision(startTime=None, endTime=None, title=None))


def test_a_naive_extracted_time_is_rejected(intent_validator):
    """An offset-less time is the one that silently lands on the wrong hour."""
    with pytest.raises(Exception):
        intent_validator.validate(_decision(startTime="2026-09-22T15:00:00"))


@pytest.mark.parametrize("value", [-0.1, 1.1])
def test_confidence_stays_in_range(intent_validator, value):
    with pytest.raises(Exception):
        intent_validator.validate(_decision(confidence=value))


def test_an_intent_outside_the_enum_is_rejected(intent_validator):
    """asking_availability was deliberately left out of the first cut."""
    with pytest.raises(Exception):
        intent_validator.validate(_decision(intent="asking_availability"))


@pytest.mark.parametrize("field", ["messageId", "reason"])
def test_required_labels_cannot_be_empty(intent_validator, field):
    with pytest.raises(Exception):
        intent_validator.validate(_decision(**{field: ""}))


def test_extra_fields_are_rejected(intent_validator):
    """additionalProperties:false -- a stray field breaks a strict consumer."""
    with pytest.raises(Exception):
        intent_validator.validate(_decision(urgency=0.5))


def test_the_request_requires_now(validator_for, load_fixture):
    """The field callers forget, and the one a model cannot supply itself."""
    request = load_fixture("intent-analyze-request.message.json")
    validator_for("intent-analyze-request.schema.json").validate(request)
    del request["now"]
    with pytest.raises(Exception):
        validator_for("intent-analyze-request.schema.json").validate(request)


def test_the_request_embeds_the_real_message_contract(validator_for, load_fixture):
    """It composes by $ref, so a bad message must fail the request too."""
    request = load_fixture("intent-analyze-request.message.json")
    request["message"]["timestamp"] = "2026-09-19T11:32:00"  # no offset
    with pytest.raises(Exception):
        validator_for("intent-analyze-request.schema.json").validate(request)
