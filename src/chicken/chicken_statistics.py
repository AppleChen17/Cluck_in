"""Canonical inventory and cumulative statistics for the chicken service."""
from __future__ import annotations
import copy
import json
import os
from pathlib import Path


def default_state_path() -> Path:
    override = os.environ.get("CLUCKIN_CHICKEN_STATE_PATH")
    if override:
        return Path(override)
    base = Path(os.environ.get("LOCALAPPDATA", Path.home() / ".local" / "share"))
    return base / "CluckIn" / "chicken-state.json"


class ChickenStatistics:
    def __init__(self, path: Path | None = None):
        self.path = path
        self.data = {
            "feedCount": 0,
            "patCount": 0,
            "successfulFeedCount": 0,
            "totalFocusSeconds": 0,
            "focusSessions": {},
        }
        if path is not None and path.exists():
            self.data.update(json.loads(path.read_text(encoding="utf-8")))
            for key in ("feedCount", "patCount", "successfulFeedCount", "totalFocusSeconds"):
                self._integer(self.data[key])

    @staticmethod
    def _integer(value):
        if type(value) is not int or not 0 <= value <= 2**63 - 1:
            raise ValueError("Expected a non-negative 64-bit integer")
        return value

    def _commit(self, data):
        if self.path is not None:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            temporary = self.path.with_suffix(".tmp")
            temporary.write_text(json.dumps(data, indent=2), encoding="utf-8")
            temporary.replace(self.path)
        self.data = data

    def snapshot(self):
        return {key: self.data[key] for key in
                ("feedCount", "patCount", "successfulFeedCount", "totalFocusSeconds")}

    def set_feed(self, count):
        data = copy.deepcopy(self.data)
        data["feedCount"] = self._integer(count)
        self._commit(data)

    def pet(self):
        data = copy.deepcopy(self.data)
        data["patCount"] += 1
        self._commit(data)

    def consume_feed(self):
        if self.data["feedCount"] == 0:
            return False
        data = copy.deepcopy(self.data)
        data["feedCount"] -= 1
        data["successfulFeedCount"] += 1
        self._commit(data)
        return True

    def record_focus(self, session_id, seconds, completed):
        if not isinstance(session_id, str) or not session_id or len(session_id) > 128:
            raise ValueError("A stable focus session ID is required")

        self._integer(seconds)

        if type(completed) is not bool:
            raise ValueError("Invalid focus completion")

        previous = self.data["focusSessions"].get(
            session_id,
            {
                "seconds": 0,
                "completed": False,
            },
        )

        previous_seconds = self._integer(
            previous.get("seconds", 0)
        )

        previous_completed = previous.get(
            "completed",
            False,
        )

        if type(previous_completed) is not bool:
            raise ValueError(
                "Invalid stored focus completion"
            )

        maximum = max(
            seconds,
            previous_seconds,
        )

        newly_completed = (
            completed
            and not previous_completed
        )

        if (
            maximum == previous_seconds
            and not newly_completed
        ):
            return

        data = copy.deepcopy(self.data)

        added_seconds = (
            maximum - previous_seconds
        )

        data["totalFocusSeconds"] += added_seconds

        previous_rewards = previous_seconds // 5
        current_rewards = maximum // 5
        earned = current_rewards - previous_rewards

        data["feedCount"] += earned

        data["focusSessions"][session_id] = {
            "seconds": maximum,
            "completed": (
                completed
                or previous_completed
            ),
        }

        self._commit(data)