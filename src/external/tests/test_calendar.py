"""The two calendar backends, and contract conformance of what they produce.

The availability arithmetic that used to dominate this file went with the
feature it served; see the commit that removed it if it is ever wanted back.
"""

from datetime import datetime, time, timedelta, timezone

import pytest

from gcal.google_calendar import to_external_event
from gcal.memory_calendar import MemoryCalendar

TPE = timezone(timedelta(hours=8))

# 2026-09-21 is a Monday, 2026-09-26 a Saturday. Fixed dates, never "today":
# a test that drifts with the calendar passes all week and fails on Sunday.
MONDAY = 21


def at(day: int, hour: int, minute: int = 0) -> datetime:
    return datetime(2026, 9, day, hour, minute, tzinfo=TPE)


# -- the memory backend -------------------------------------------------------


def test_a_created_event_is_immediately_listed():
    calendar = MemoryCalendar()
    calendar.create_event(title="Sprint review", start=at(MONDAY, 14), end=at(MONDAY, 15))
    listed = calendar.events(at(MONDAY, 0), at(MONDAY + 1, 0))
    assert [e.title for e in listed] == ["Sprint review"]


def test_an_event_overlapping_the_window_edge_still_counts():
    """Containment would miss a meeting already under way at the window start."""
    calendar = MemoryCalendar()
    calendar.create_event(title="Long one", start=at(MONDAY, 8), end=at(MONDAY, 12))
    assert calendar.events(at(MONDAY, 10), at(MONDAY, 11))


def test_events_outside_the_window_are_excluded():
    calendar = MemoryCalendar()
    calendar.create_event(title="Next week", start=at(MONDAY + 7, 14), end=at(MONDAY + 7, 15))
    assert calendar.events(at(MONDAY, 0), at(MONDAY + 1, 0)) == []


def test_events_come_back_oldest_first():
    calendar = MemoryCalendar()
    calendar.create_event(title="Later", start=at(MONDAY, 16), end=at(MONDAY, 17))
    calendar.create_event(title="Earlier", start=at(MONDAY, 9), end=at(MONDAY, 10))
    assert [e.title for e in calendar.events(at(MONDAY, 0), at(MONDAY + 1, 0))] == [
        "Earlier",
        "Later",
    ]


def test_a_created_event_validates_against_the_contract(validator_for):
    calendar = MemoryCalendar(tz_mode="local")
    event = calendar.create_event(
        title="Cluck In demo rehearsal",
        start=at(MONDAY, 14),
        end=at(MONDAY, 15),
        description=None,
        location=None,
        attendees=["alice@example.com"],
        metadata={"fromMessageId": "slack:C1:1.0"},
    )
    validator_for("external-event.schema.json").validate(event.model_dump())


def test_an_event_carries_no_stray_keys():
    """additionalProperties:false -- one helper field breaks strict consumers."""
    calendar = MemoryCalendar()
    event = calendar.create_event(title="x", start=at(MONDAY, 9), end=at(MONDAY, 10))
    allowed = {
        "id", "source", "title", "description", "startTime",
        "endTime", "location", "attendees", "metadata",
    }
    assert set(event.model_dump()) <= allowed


def test_no_attendees_is_an_empty_list_not_an_absent_field():
    """The contract distinguishes them: [] means nobody, absent means unknown."""
    calendar = MemoryCalendar()
    event = calendar.create_event(title="x", start=at(MONDAY, 9), end=at(MONDAY, 10))
    assert event.model_dump()["attendees"] == []


# -- mapping Google responses -------------------------------------------------


def test_a_google_event_maps_onto_the_contract(validator_for):
    raw = {
        "id": "abc123",
        "summary": "Sprint review",
        "description": "Weekly",
        "location": "Room 3",
        "start": {"dateTime": "2026-09-21T14:00:00+08:00"},
        "end": {"dateTime": "2026-09-21T15:00:00+08:00"},
        "attendees": [{"displayName": "Alice"}, {"email": "bob@example.com"}],
        "htmlLink": "https://calendar.google.com/event?eid=x",
    }
    event = to_external_event(raw, "primary", "local")
    validator_for("external-event.schema.json").validate(event.model_dump())
    assert event.id == "google-calendar:abc123"
    # Display name preferred, address as the fallback -- labels, not identities.
    assert event.attendees == ["Alice", "bob@example.com"]
    assert event.metadata["calendarId"] == "primary"


def test_all_day_events_are_skipped_not_coerced():
    """A date-only value has no offset and would fail the contract; inventing
    midnight would block the whole day."""
    raw = {"id": "d1", "summary": "Holiday",
           "start": {"date": "2026-09-21"}, "end": {"date": "2026-09-22"}}
    assert to_external_event(raw, "primary") is None


def test_an_untitled_google_event_gets_a_usable_title():
    """The contract needs minLength 1; the Google UI says "(no title)" too."""
    raw = {"id": "x", "start": {"dateTime": "2026-09-21T14:00:00+08:00"},
           "end": {"dateTime": "2026-09-21T15:00:00+08:00"}}
    event = to_external_event(raw, "primary")
    assert event.title


@pytest.mark.parametrize(
    "raw",
    [
        {"id": "", "start": {"dateTime": "2026-09-21T14:00:00+08:00"},
         "end": {"dateTime": "2026-09-21T15:00:00+08:00"}},
        {"id": "x", "start": {"dateTime": "2026-09-21T15:00:00+08:00"},
         "end": {"dateTime": "2026-09-21T14:00:00+08:00"}},
        {"id": "x", "start": {"dateTime": "not a date"},
         "end": {"dateTime": "2026-09-21T15:00:00+08:00"}},
    ],
)
def test_unusable_google_events_are_skipped(raw):
    assert to_external_event(raw, "primary") is None
