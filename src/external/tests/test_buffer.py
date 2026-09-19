import threading

import pytest

from buffer import MessageBuffer, encode_cursor, parse_cursor
from schemas import ExternalMessage


def make(idx: int, ts: str = "2026-09-19T11:31:00+08:00") -> ExternalMessage:
    return ExternalMessage(
        id=f"gmail:{idx}", source="gmail", sender="A", title=None,
        content=f"body {idx}", timestamp=ts, unread=True,
    )


def test_add_dedups_by_id():
    buf = MessageBuffer(maxlen=10)
    assert buf.add(make(1)) is True
    assert buf.add(make(1)) is False
    assert buf.stats()["buffered"] == 1


def test_dedup_does_not_advance_the_sequence():
    buf = MessageBuffer(maxlen=10)
    buf.add(make(1))
    first = buf.latest_seq()
    buf.add(make(1))
    assert buf.latest_seq() == first


def test_reads_are_idempotent():
    buf = MessageBuffer(maxlen=10)
    for i in range(3):
        buf.add(make(i))
    a, last, _ = buf.read_after(0, 100)
    b, last_again, _ = buf.read_after(0, 100)
    assert [m.id for m in a] == [m.id for m in b]
    assert last == last_again


def test_cursor_advances_past_delivered_messages():
    buf = MessageBuffer(maxlen=10)
    buf.add(make(1))
    items, last, _ = buf.read_after(0, 100)
    assert len(items) == 1
    buf.add(make(2))
    items, last2, _ = buf.read_after(last, 100)
    assert [m.id for m in items] == ["gmail:2"]
    assert buf.read_after(last2, 100)[0] == []


def test_limit_reports_has_more():
    buf = MessageBuffer(maxlen=10)
    for i in range(5):
        buf.add(make(i))
    items, last, has_more = buf.read_after(0, 2)
    assert len(items) == 2 and has_more is True
    items, _, has_more = buf.read_after(last, 100)
    assert len(items) == 3 and has_more is False


def test_since_filters_without_acting_as_a_cursor():
    buf = MessageBuffer(maxlen=10)
    buf.add(make(1, "2026-09-19T10:00:00+08:00"))
    buf.add(make(2, "2026-09-19T12:00:00+08:00"))
    items, _, _ = buf.read_after(0, 100, since="2026-09-19T11:00:00+08:00")
    assert [m.id for m in items] == ["gmail:2"]


def test_out_of_order_send_time_is_still_delivered():
    """A delayed email arrives with an older send time than one already sent.

    A timestamp watermark would swallow it forever; a sequence cursor does not.
    """
    buf = MessageBuffer(maxlen=10)
    buf.add(make(1, "2026-09-19T12:00:00+08:00"))
    _, last, _ = buf.read_after(0, 100)
    buf.add(make(2, "2026-09-19T10:00:00+08:00"))
    items, _, _ = buf.read_after(last, 100)
    assert [m.id for m in items] == ["gmail:2"]


def test_seen_set_stays_bounded():
    buf = MessageBuffer(maxlen=4)
    for i in range(100):
        buf.add(make(i))
    stats = buf.stats()
    assert stats["buffered"] == 4
    assert stats["seen"] <= 16


def test_concurrent_adds_assign_unique_sequences():
    buf = MessageBuffer(maxlen=5000)
    errors: list[BaseException] = []

    def worker(base: int) -> None:
        try:
            for i in range(500):
                buf.add(make(base * 1000 + i))
        except BaseException as exc:  # noqa: BLE001
            errors.append(exc)

    threads = [threading.Thread(target=worker, args=(t,)) for t in range(8)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()

    assert not errors
    items, _, _ = buf.read_after(0, 5000)
    assert len(items) == 4000
    assert len({m.id for m in items}) == 4000


@pytest.mark.parametrize(
    "raw,expected",
    [
        (None, (None, 0)),
        ("", (None, 0)),
        ("garbage", (None, 0)),
        ("127", (None, 0)),
        ("2026-09-19T11:31:00+08:00", (None, 0)),
        ("v1:abc12345:127", ("abc12345", 127)),
    ],
)
def test_parse_cursor(raw, expected):
    assert parse_cursor(raw) == expected


def test_cursor_roundtrip():
    assert parse_cursor(encode_cursor("abc12345", 9)) == ("abc12345", 9)
