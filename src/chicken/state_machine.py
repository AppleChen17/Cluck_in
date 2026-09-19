"""Chicken moods for AppState.chicken.mood (MVP spec §10)."""

from __future__ import annotations

from pathlib import Path

FRAMES_DIR = Path(__file__).resolve().parent / "frames"

# Schema moods plus feed (餵食，一次性吃東西動畫).
MOODS = ("idle", "focused", "thinking", "feed", "pet", "paused", "tired")
ONESHOT_MOODS = frozenset({"feed", "pet"})
NEST_MOODS = frozenset({"paused"})  # 雞畫在第六格巢裡
# 書桌+鳥巢只存在番茄鐘場景；idle / feed 沒有巢。
SESSION_MOODS = frozenset({"focused", "thinking", "pet", "paused"})

def _frame_count(mood: str, fallback: int) -> int:
    n = len(list(FRAMES_DIR.glob(f"{mood}_*.png")))
    return n if n > 0 else fallback


FRAME_COUNTS = {
    "idle": _frame_count("idle", 4),
    "focused": _frame_count("focused", 4),
    "thinking": _frame_count("thinking", 2),
    "feed": _frame_count("feed", 2),
    "pet": _frame_count("pet", 12),
    "paused": _frame_count("paused", 5),
    "tired": _frame_count("tired", 2),
}

# Spec §10 fps. logitech can use this when pushing button 5.
FPS = {
    "idle": 2,
    "focused": 3,
    "thinking": 4,
    "feed": 4,
    "pet": 4,
    "paused": 2,
    "tired": 1,
}

FEED_TICKS = FRAME_COUNTS["feed"]
PET_TICKS = FRAME_COUNTS["pet"]


class ChickenAnim:
    def __init__(self) -> None:
        self.mood = "idle"
        self._return_to = "idle"
        self.frame = 0
        self._oneshot_left = 0
        self.feed_count = 0

    def handle_event(self, event: str, payload: dict | None = None) -> dict:
        payload = payload or {}
        name = event.strip()

        if name == "tick":
            self._tick()
        elif name in {"START_FOCUS", "startFocus"}:
            self._enter("focused")
        elif name in {"PAUSE_FOCUS", "pauseFocus", "pause"}:
            self._enter("paused")
        elif name in {"RESUME_FOCUS", "resumeFocus", "resume"}:
            self._enter("focused")
        elif name in {"STOP_FOCUS", "endFocus", "roam"}:
            self._enter("idle")
        elif name in {"PET_CHICKEN", "pet", "pat", "pad"}:
            self._oneshot("pet", PET_TICKS)
        elif name in {"FEED_CHICKEN", "feed"}:
            if self.feed_count > 0:
                self.feed_count -= 1
            self._oneshot("feed", FEED_TICKS, return_to="idle")
        elif name in {"SET_MOOD", "setMood"}:
            mood = str(payload.get("mood", self.mood))
            self._enter(mood)
        elif name == "SET_FEED":
            self.feed_count = int(payload.get("count", self.feed_count))
        else:
            raise ValueError(f"unknown event: {event}")
        return self.get_view()

    def get_view(self) -> dict:
        filename = f"{self.mood}_{self.frame:02d}.png"
        in_nest = self.mood in NEST_MOODS
        if in_nest:
            nest_icon = filename
        elif self.mood in SESSION_MOODS:
            nest_icon = "nest_icon.png"
        else:
            nest_icon = ""
        return {
            "mood": self.mood,
            "chicken": filename,
            "fps": FPS[self.mood],
            "loop": self.mood not in ONESHOT_MOODS,
            "frameDir": str(FRAMES_DIR),
            "displayKey": 6 if in_nest else 5,
            "nestIcon": nest_icon,
            "deskEmpty": "desk_empty.png",
            "feedCount": self.feed_count,
        }

    def _enter(self, mood: str) -> None:
        if mood not in FRAME_COUNTS:
            raise ValueError(f"unknown mood: {mood}")
        self.mood = mood
        self._return_to = mood if mood not in ONESHOT_MOODS else self._return_to
        self.frame = 0
        self._oneshot_left = 0

    def _oneshot(self, mood: str, ticks: int, return_to: str | None = None) -> None:
        if return_to is not None:
            self._return_to = return_to
        elif self.mood not in {"thinking", *ONESHOT_MOODS}:
            self._return_to = self.mood
        self.mood = mood
        self.frame = 0
        self._oneshot_left = ticks

    def _tick(self) -> None:
        count = FRAME_COUNTS[self.mood]
        self.frame = (self.frame + 1) % count
        if self._oneshot_left <= 0:
            return
        self._oneshot_left -= 1
        if self._oneshot_left > 0:
            return
        self._enter(self._return_to)


_anim = ChickenAnim()


def handle_event(event: str, payload: dict | None = None) -> dict:
    return _anim.handle_event(event, payload)


def get_view() -> dict:
    return _anim.get_view()


def reset() -> None:
    global _anim
    _anim = ChickenAnim()
