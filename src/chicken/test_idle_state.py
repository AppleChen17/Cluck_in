"""Idle inventory, persistence, contracts and stable animation framing."""
import json
from pathlib import Path
import tempfile
import unittest

from PIL import Image
from jsonschema import Draft202012Validator

from chicken_statistics import ChickenStatistics
from state_machine import ChickenAnim, FRAME_COUNTS, FRAMES_DIR
from animation_layout import idle_crop


class IdleStateTests(unittest.TestCase):
    @staticmethod
    def _advance_until_mood(anim, expected_mood, max_ticks=256):
        for _ in range(max_ticks):
            if anim.mood == expected_mood:
                return
            anim.handle_event("tick")

        raise AssertionError(
            f"Animation did not return to {expected_mood!r}; "
            f"current mood is {anim.mood!r}"
        )

    def test_inventory_and_successful_actions(self):
        anim = ChickenAnim()

        anim.handle_event("FEED_CHICKEN")
        self.assertEqual(anim.mood, "idle")
        self.assertEqual(
            anim.get_view()["successfulFeedCount"],
            0,
        )

        anim.handle_event("PET_CHICKEN")
        self.assertEqual(
            anim.get_view()["patCount"],
            1,
        )

        self._advance_until_mood(
            anim,
            "idle",
        )

        anim.handle_event(
            "SET_FEED",
            {"count": 1},
        )

        anim.handle_event("FEED_CHICKEN")

        self.assertEqual(
            anim.mood,
            "feed",
        )
        self.assertEqual(
            anim.feed_count,
            0,
        )
        self.assertEqual(
            anim.get_view()["successfulFeedCount"],
            1,
        )

        self._advance_until_mood(
            anim,
            "idle",
        )

        for value in (-1, True, 1.5):
            with self.assertRaises(ValueError):
                anim.handle_event(
                    "SET_FEED",
                    {"count": value},
                )

    def test_per_session_focus_rewards(self):
        statistics = ChickenStatistics()

        statistics.record_focus(
            "session-a",
            6,
            True,
        )

        self.assertEqual(
            statistics.data["feedCount"],
            1,
        )

        statistics.record_focus(
            "session-b",
            4,
            True,
        )

        self.assertEqual(
            statistics.data["feedCount"],
            1,
        )

        statistics.record_focus(
            "session-c",
            10,
            True,
        )

        self.assertEqual(
            statistics.data["feedCount"],
            3,
        )

        self.assertEqual(
            statistics.data["totalFocusSeconds"],
            20,
        )

        statistics.record_focus(
            "session-c",
            10,
            True,
        )

        self.assertEqual(
            statistics.data["feedCount"],
            3,
        )
        self.assertEqual(
            statistics.data["totalFocusSeconds"],
            20,
        )

        statistics.record_focus(
            "session-d",
            4,
            False,
        )

        self.assertEqual(
            statistics.data["feedCount"],
            3,
        )

        statistics.record_focus(
            "session-d",
            5,
            False,
        )

        self.assertEqual(
            statistics.data["feedCount"],
            4,
        )

        statistics.record_focus(
            "session-d",
            9,
            False,
        )

        self.assertEqual(
            statistics.data["feedCount"],
            4,
        )

        statistics.record_focus(
            "session-d",
            10,
            True,
        )

        self.assertEqual(
            statistics.data["feedCount"],
            5,
        )

    def test_persistence_and_idempotent_focus_credit(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"

            anim = ChickenAnim(
                ChickenStatistics(path)
            )

            anim.handle_event(
                "RECORD_FOCUS",
                {
                    "sessionId": "a",
                    "seconds": 12,
                    "completed": False,
                },
            )

            self.assertEqual(
                anim.feed_count,
                2,
            )

            anim.handle_event(
                "RECORD_FOCUS",
                {
                    "sessionId": "a",
                    "seconds": 20,
                    "completed": True,
                },
            )

            self.assertEqual(
                anim.feed_count,
                4,
            )

            anim.handle_event("PET_CHICKEN")
            anim.handle_event("FEED_CHICKEN")

            restored = ChickenAnim(
                ChickenStatistics(path)
            )

            restored.handle_event(
                "RECORD_FOCUS",
                {
                    "sessionId": "a",
                    "seconds": 20,
                    "completed": True,
                },
            )

            restored.handle_event(
                "RECORD_FOCUS",
                {
                    "sessionId": "a",
                    "seconds": 5,
                    "completed": False,
                },
            )

            restored.handle_event(
                "RECORD_FOCUS",
                {
                    "sessionId": "early-stop",
                    "seconds": 3,
                    "completed": False,
                },
            )

            self.assertEqual(
                restored.statistics.snapshot(),
                {
                    "feedCount": 3,
                    "patCount": 1,
                    "successfulFeedCount": 1,
                    "totalFocusSeconds": 23,
                },
            )

            before = path.read_bytes()

            restored.handle_event("tick")

            self.assertEqual(
                path.read_bytes(),
                before,
            )

    def test_existing_animation_transitions(self):
        anim = ChickenAnim()

        expected_idle_frames = {
            f"idle_{index:02d}.png"
            for index in range(
                FRAME_COUNTS["idle"]
            )
        }

        observed_idle_frames = {
            anim.handle_event("tick")["chicken"]
            for _ in range(
                max(
                    32,
                    FRAME_COUNTS["idle"] * 8,
                )
            )
        }

        self.assertTrue(
            expected_idle_frames.issubset(
                observed_idle_frames
            ),
            (
                "Idle animation did not cycle through "
                f"all frames: {observed_idle_frames}"
            ),
        )

        anim.handle_event("START_FOCUS")

        self.assertEqual(
            anim.mood,
            "focused",
        )

        anim.handle_event("PET_CHICKEN")

        self._advance_until_mood(
            anim,
            "focused",
        )

        anim.handle_event("PAUSE_FOCUS")

        self.assertEqual(
            anim.get_view()["displayKey"],
            5,
        )

    def test_common_crop_keeps_every_visible_pixel(self):
        crop = idle_crop()

        self.assertEqual(
            crop,
            {
                "x": 7,
                "y": 76,
                "width": 49,
                "height": 52,
            },
        )

        for mood in (
            "idle",
            "pet",
            "feed",
        ):
            for path in FRAMES_DIR.glob(
                mood + "_*.png"
            ):
                with Image.open(path) as image:
                    left, top, right, bottom = (
                        image
                        .getchannel("A")
                        .getbbox()
                    )

                    self.assertGreaterEqual(
                        left,
                        crop["x"],
                        path.name,
                    )
                    self.assertGreaterEqual(
                        top,
                        crop["y"],
                        path.name,
                    )
                    self.assertLessEqual(
                        right,
                        crop["x"]
                        + crop["width"],
                        path.name,
                    )
                    self.assertLessEqual(
                        bottom,
                        crop["y"]
                        + crop["height"],
                        path.name,
                    )

    def test_shared_schemas_and_fixtures(self):
        root = (
            Path(__file__)
            .resolve()
            .parents[2]
        )

        for kind in (
            "view",
            "event",
        ):
            schema_path = (
                root
                / f"shared/schemas/chicken-{kind}.schema.json"
            )

            schema = json.loads(
                schema_path.read_text(
                    encoding="utf-8"
                )
            )

            validator = Draft202012Validator(
                schema
            )

            validator.check_schema(
                schema
            )

            fixtures = (
                root
                / "shared/fixtures"
            ).glob(
                f"chicken-{kind}-*.json"
            )

            for fixture in fixtures:
                validator.validate(
                    json.loads(
                        fixture.read_text(
                            encoding="utf-8"
                        )
                    )
                )

            if kind == "view":
                view = ChickenAnim().get_view()
                view["idleCrop"] = idle_crop()

                validator.validate(
                    view
                )


if __name__ == "__main__":
    unittest.main()

