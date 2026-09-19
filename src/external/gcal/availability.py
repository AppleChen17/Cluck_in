"""Working out when you are free. Pure: no network, no config, no state.

This is deliberately not the model's job. "Which half-hours next week are free,
inside working hours, on weekdays, that do not collide with these fourteen
existing events" is arithmetic, and a small local model gets arithmetic wrong in
ways that are invisible until someone shows up on the wrong day. The model reads
the question and writes the sentence; this computes the answer it quotes.

Aware datetimes throughout. The offset comes from window_start, which is how the
caller chooses local or UTC without this module knowing about either.
"""

from dataclasses import dataclass
from datetime import date, datetime, time, timedelta

_WEEKDAY_ZH = ("一", "二", "三", "四", "五", "六", "日")


@dataclass(frozen=True)
class Slot:
    start: datetime
    end: datetime


def merge_busy(
    intervals: list[tuple[datetime, datetime]],
) -> list[tuple[datetime, datetime]]:
    """Sort and coalesce overlapping or touching busy intervals.

    Overlaps are normal, not a data error: a calendar happily holds two meetings
    at once, and Google freebusy returns both. Subtracting them one at a time
    without merging first produces zero-length or negative free gaps.
    """
    usable = [(s, e) for s, e in intervals if e > s]
    if not usable:
        return []
    usable.sort(key=lambda pair: pair[0])
    merged = [usable[0]]
    for start, end in usable[1:]:
        last_start, last_end = merged[-1]
        if start <= last_end:
            merged[-1] = (last_start, max(last_end, end))
        else:
            merged.append((start, end))
    return merged


def _subtract(
    window: tuple[datetime, datetime],
    busy: list[tuple[datetime, datetime]],
) -> list[tuple[datetime, datetime]]:
    """The parts of the window that no merged busy interval covers."""
    start, end = window
    free: list[tuple[datetime, datetime]] = []
    cursor = start
    for busy_start, busy_end in busy:
        if busy_end <= cursor or busy_start >= end:
            continue
        if busy_start > cursor:
            free.append((cursor, busy_start))
        cursor = max(cursor, busy_end)
        if cursor >= end:
            return free
    if cursor < end:
        free.append((cursor, end))
    return free


def _ceil_to_granularity(moment: datetime, minutes: int) -> datetime:
    """Round up to the next :00 / :30 (or whatever the step is) of the hour.

    Aligned to the wall clock rather than to the start of the free gap, so the
    proposals read as 14:00 and 14:30 rather than 14:07 and 14:37. Nobody
    schedules a meeting at 14:07.
    """
    if minutes <= 0:
        return moment
    if moment.second or moment.microsecond:
        moment = moment.replace(second=0, microsecond=0) + timedelta(minutes=1)
    offset = moment.minute % minutes
    return moment if offset == 0 else moment + timedelta(minutes=minutes - offset)


def free_slots(
    busy: list[tuple[datetime, datetime]],
    *,
    window_start: datetime,
    window_end: datetime,
    duration_minutes: int = 30,
    workday_start: time = time(9, 0),
    workday_end: time = time(18, 0),
    weekdays_only: bool = True,
    granularity_minutes: int = 30,
    limit: int = 5,
    max_per_day: int = 2,
    spacing_minutes: int = 120,
) -> list[Slot]:
    """Candidate meeting slots, earliest first.

    Two rules keep the list readable rather than merely correct, because the
    output of this function is pasted into a sentence a person reads:

      * max_per_day spreads proposals across days. Offering 09:00, 09:30, 10:00,
        10:30 and 11:00 on one Monday is technically five options and
        practically one.
      * spacing_minutes separates the proposals within a day, so a free morning
        yields 09:00 and 11:00 rather than two touching half-hours.
    """
    if window_end <= window_start or duration_minutes <= 0 or limit <= 0:
        return []

    tz = window_start.tzinfo
    duration = timedelta(minutes=duration_minutes)
    merged = merge_busy(busy)
    slots: list[Slot] = []

    day: date = window_start.date()
    last_day: date = window_end.date()
    while day <= last_day and len(slots) < limit:
        if weekdays_only and day.weekday() >= 5:
            day += timedelta(days=1)
            continue

        day_open = datetime.combine(day, workday_start).replace(tzinfo=tz)
        day_close = datetime.combine(day, workday_end).replace(tzinfo=tz)
        # An overnight working window (22:00 to 06:00) is not modelled; it would
        # need the close time to roll into the next day, and no demo needs it.
        if day_close <= day_open:
            day += timedelta(days=1)
            continue

        bounded = (max(day_open, window_start), min(day_close, window_end))
        spacing = max(duration, timedelta(minutes=max(spacing_minutes, 0)))
        today_count = 0
        last_start: datetime | None = None
        for gap_start, gap_end in _subtract(bounded, merged):
            cursor = _ceil_to_granularity(gap_start, granularity_minutes)
            while cursor + duration <= gap_end:
                if last_start is None or cursor - last_start >= spacing:
                    slots.append(Slot(cursor, cursor + duration))
                    last_start = cursor
                    today_count += 1
                    if today_count >= max_per_day or len(slots) >= limit:
                        break
                cursor += timedelta(minutes=granularity_minutes)
            if today_count >= max_per_day or len(slots) >= limit:
                break
        day += timedelta(days=1)

    return slots[:limit]


def format_slots(slots: list[Slot]) -> str:
    """Render slots the way a person would write them, grouped by day.

    Traditional Chinese, because that is the language the messages this module
    sees are written in. Returns an empty string for no slots: what to say
    instead is the caller decision, not this function.
    """
    if not slots:
        return ""
    by_day: dict[date, list[Slot]] = {}
    for slot in slots:
        by_day.setdefault(slot.start.date(), []).append(slot)
    lines = []
    for day, day_slots in by_day.items():
        times = "、".join(
            "{:%H:%M}-{:%H:%M}".format(s.start, s.end) for s in day_slots
        )
        lines.append(
            "{}/{}（{}）{}".format(
                day.month, day.day, _WEEKDAY_ZH[day.weekday()], times
            )
        )
    return "\n".join(lines)
