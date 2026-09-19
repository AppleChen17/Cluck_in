"""Availability arithmetic, the two backends, and contract conformance.

The arithmetic tests carry the most weight in this file. Everything downstream
quotes their output verbatim into a message a person will act on, so a slot that
collides with an existing meeting becomes someone sitting in the wrong room.
"""

from datetime import datetime, time, timedelta, timezone

import pytest

from gcal.availability import Slot, format_slots, free_slots, merge_busy
from gcal.google_calendar import to_external_event
from gcal.memory_calendar import MemoryCalendar

TPE = timezone(timedelta(hours=8))

# 2026-09-21 is a Monday, 2026-09-26 a Saturday. Fixed dates, never "today":
# a test that drifts with the calendar passes all week and fails on Sunday.
MONDAY = 21


def at(day: int, hour: int, minute: int = 0) -> datetime:
    return datetime(2026, 9, day, hour, minute, tzinfo=TPE)


# -- merging ------------------------------------------------------------------


def test_overlapping_busy_blocks_merge():
    """Two meetings at once is normal on a real calendar, not bad data."""
    merged = merge_busy([(at(MONDAY, 10), at(MONDAY, 12)), (at(MONDAY, 11), at(MONDAY, 13))])
    assert merged == [(at(MONDAY, 10), at(MONDAY, 13))]


def test_touching_busy_blocks_merge():
    merged = merge_busy([(at(MONDAY, 10), at(MONDAY, 11)), (at(MONDAY, 11), at(MONDAY, 12))])
    assert merged == [(at(MONDAY, 10), at(MONDAY, 12))]


def test_zero_length_busy_blocks_are_dropped():
    assert merge_busy([(at(MONDAY, 10), at(MONDAY, 10))]) == []


def test_busy_blocks_arrive_in_any_order():
    merged = merge_busy([(at(MONDAY, 15), at(MONDAY, 16)), (at(MONDAY, 9), at(MONDAY, 10))])
    assert merged[0][0] == at(MONDAY, 9)


# -- slots --------------------------------------------------------------------


def test_a_slot_never_overlaps_a_busy_block():
    """The invariant the whole feature rests on."""
    busy = [(at(MONDAY, 9, 30), at(MONDAY, 12)), (at(MONDAY, 14), at(MONDAY, 17, 30))]
    slots = free_slots(
        busy,
        window_start=at(MONDAY, 8),
        window_end=at(MONDAY, 23),
        duration_minutes=30,
        limit=10,
    )
    for slot in slots:
        for busy_start, busy_end in busy:
            assert slot.end <= busy_start or slot.start >= busy_end


def test_slots_stay_inside_working_hours():
    slots = free_slots(
        [],
        window_start=at(MONDAY, 0),
        window_end=at(MONDAY + 1, 0),
        duration_minutes=60,
        workday_start=time(9, 0),
        workday_end=time(18, 0),
        limit=10,
    )
    assert slots
    for slot in slots:
        assert slot.start.hour >= 9
        assert slot.end.hour <= 18


def test_weekends_are_skipped_by_default():
    # 26th is a Saturday, 27th a Sunday.
    assert free_slots([], window_start=at(26, 0), window_end=at(27, 23), limit=5) == []


def test_weekends_can_be_included():
    slots = free_slots(
        [], window_start=at(26, 0), window_end=at(27, 23), weekdays_only=False, limit=5
    )
    assert slots


def test_slots_align_to_the_clock_not_to_the_gap():
    """A gap opening at 14:07 proposes 14:30. Nobody meets at 14:07."""
    slots = free_slots(
        [(at(MONDAY, 9), at(MONDAY, 14, 7))],
        window_start=at(MONDAY, 9),
        window_end=at(MONDAY, 18),
        duration_minutes=30,
        granularity_minutes=30,
        limit=1,
    )
    assert slots[0].start == at(MONDAY, 14, 30)


def test_proposals_spread_across_days_rather_than_stacking():
    """Five half-hours on one Monday is one option dressed as five."""
    slots = free_slots(
        [], window_start=at(MONDAY, 8), window_end=at(MONDAY + 4, 20), limit=5, max_per_day=2
    )
    per_day = {}
    for slot in slots:
        per_day[slot.start.date()] = per_day.get(slot.start.date(), 0) + 1
    assert max(per_day.values()) <= 2
    assert len(per_day) > 1


def test_proposals_within_a_day_are_spaced_apart():
    slots = free_slots(
        [],
        window_start=at(MONDAY, 8),
        window_end=at(MONDAY, 20),
        duration_minutes=30,
        spacing_minutes=120,
        max_per_day=2,
        limit=2,
    )
    assert (slots[1].start - slots[0].start) >= timedelta(minutes=120)


def test_a_fully_booked_window_yields_nothing():
    slots = free_slots(
        [(at(MONDAY, 0), at(MONDAY + 7, 0))],
        window_start=at(MONDAY, 8),
        window_end=at(MONDAY + 4, 18),
        limit=5,
    )
    assert slots == []


def test_nothing_is_proposed_before_the_window_starts():
    """The window start carries the lead time; an earlier slot would undo it."""
    slots = free_slots(
        [], window_start=at(MONDAY, 14, 15), window_end=at(MONDAY, 18), limit=5
    )
    assert all(slot.start >= at(MONDAY, 14, 15) for slot in slots)


def test_a_meeting_longer_than_any_gap_yields_nothing():
    """Two one-hour gaps do not add up to one two-hour slot."""
    slots = free_slots(
        [(at(MONDAY, 10), at(MONDAY, 16)), (at(MONDAY, 17), at(MONDAY, 23))],
        window_start=at(MONDAY, 9),
        window_end=at(MONDAY, 18),
        duration_minutes=120,
        limit=5,
    )
    assert slots == []


@pytest.mark.parametrize(
    "kwargs",
    [
        {"duration_minutes": 0},
        {"limit": 0},
    ],
)
def test_degenerate_requests_return_nothing_rather_than_looping(kwargs):
    assert free_slots([], window_start=at(MONDAY, 9), window_end=at(MONDAY, 18), **kwargs) == []


def test_a_backwards_window_returns_nothing():
    assert free_slots([], window_start=at(MONDAY, 18), window_end=at(MONDAY, 9)) == []


# -- rendering ----------------------------------------------------------------


def test_slots_render_grouped_by_day_in_chinese():
    text = format_slots(
        [
            Slot(at(MONDAY, 9), at(MONDAY, 9, 30)),
            Slot(at(MONDAY, 14), at(MONDAY, 14, 30)),
            Slot(at(MONDAY + 1, 10), at(MONDAY + 1, 10, 30)),
        ]
    )
    lines = text.splitlines()
    assert len(lines) == 2
    assert lines[0] == "9/21（一）09:00-09:30、14:00-14:30"
    assert lines[1].startswith("9/22（二）")


def test_no_slots_renders_empty_so_the_caller_chooses_the_wording():
    assert format_slots([]) == ""


# -- the memory backend -------------------------------------------------------


def test_a_created_event_is_immediately_busy():
    calendar = MemoryCalendar()
    calendar.create_event(title="Sprint review", start=at(MONDAY, 14), end=at(MONDAY, 15))
    assert calendar.busy(at(MONDAY, 0), at(MONDAY + 1, 0)) == [(at(MONDAY, 14), at(MONDAY, 15))]


def test_an_event_overlapping_the_window_edge_still_counts():
    """Containment would miss a meeting already under way at the window start."""
    calendar = MemoryCalendar()
    calendar.create_event(title="Long one", start=at(MONDAY, 8), end=at(MONDAY, 12))
    assert calendar.busy(at(MONDAY, 10), at(MONDAY, 11))


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
