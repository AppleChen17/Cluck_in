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
    def test_inventory_and_successful_actions(self):
        anim = ChickenAnim()
        anim.handle_event("FEED_CHICKEN")
        self.assertEqual(anim.mood, "idle")
        self.assertEqual(anim.get_view()["successfulFeedCount"], 0)
        anim.handle_event("PET_CHICKEN")
        self.assertEqual(anim.get_view()["patCount"], 1)
        for _ in range(FRAME_COUNTS["pet"]):
            anim.handle_event("tick")
        self.assertEqual(anim.mood, "idle")
        anim.handle_event("SET_FEED", {"count": 1})
        anim.handle_event("FEED_CHICKEN")
        self.assertEqual(anim.mood, "feed")
        self.assertEqual(anim.feed_count, 0)
        self.assertEqual(anim.get_view()["successfulFeedCount"], 1)
        for _ in range(FRAME_COUNTS["feed"] * 2):
            anim.handle_event("tick")
        self.assertEqual(anim.mood, "idle")
        for value in (-1, True, 1.5):
            with self.assertRaises(ValueError):
                anim.handle_event("SET_FEED", {"count": value})

    def test_persistence_and_idempotent_focus_credit(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            anim = ChickenAnim(ChickenStatistics(path))
            anim.handle_event("RECORD_FOCUS", {"sessionId": "a", "seconds": 12, "completed": False})
            self.assertEqual(anim.feed_count, 2)
            anim.handle_event("RECORD_FOCUS", {"sessionId": "a", "seconds": 20, "completed": True})
            anim.handle_event("PET_CHICKEN")
            anim.handle_event("FEED_CHICKEN")
            restored = ChickenAnim(ChickenStatistics(path))
            restored.handle_event("RECORD_FOCUS", {"sessionId": "a", "seconds": 20, "completed": True})
            restored.handle_event("RECORD_FOCUS", {"sessionId": "a", "seconds": 5, "completed": False})
            restored.handle_event("RECORD_FOCUS", {"sessionId": "early-stop", "seconds": 3, "completed": False})
            self.assertEqual(restored.statistics.snapshot(), {
                "feedCount": 3, "patCount": 1, "successfulFeedCount": 1, "totalFocusSeconds": 23})
            before = path.read_bytes()
            restored.handle_event("tick")
            self.assertEqual(path.read_bytes(), before)

    def test_focus_feed_rewards_follow_cumulative_five_second_milestones(self):
        for seconds, expected in ((0, 0), (4, 0), (5, 1), (9, 1), (10, 2), (11, 2)):
            with self.subTest(seconds=seconds):
                statistics = ChickenStatistics()
                statistics.record_focus("session", seconds, False)
                self.assertEqual(statistics.snapshot()["feedCount"], expected)

        statistics = ChickenStatistics()
        statistics.record_focus("first", 3, True)
        statistics.record_focus("second", 2, False)
        self.assertEqual(statistics.snapshot()["feedCount"], 1)

        statistics = ChickenStatistics()
        statistics.record_focus("first", 8, True)
        statistics.record_focus("second", 2, False)
        self.assertEqual(statistics.snapshot()["feedCount"], 2)

        statistics = ChickenStatistics()
        statistics.record_focus("session", 4, False)
        statistics.record_focus("session", 11, False)
        self.assertEqual(statistics.snapshot()["feedCount"], 2)
        statistics.record_focus("session", 11, False)
        self.assertEqual(statistics.snapshot()["feedCount"], 2)

    def test_focus_feed_rewards_are_idempotent_and_survive_restart_and_consumption(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            statistics = ChickenStatistics(path)
            statistics.record_focus("session", 11, False)
            statistics.record_focus("session", 11, False)
            self.assertEqual(statistics.snapshot()["feedCount"], 2)

            restarted = ChickenStatistics(path)
            restarted.record_focus("session", 11, False)
            self.assertEqual(restarted.snapshot()["feedCount"], 2)
            restarted.record_focus("session", 15, True)
            self.assertEqual(restarted.snapshot()["feedCount"], 3)

            self.assertTrue(restarted.consume_feed())
            restarted.record_focus("session", 15, True)
            self.assertEqual(restarted.snapshot()["feedCount"], 2)

    def test_old_statistics_state_does_not_reaward_processed_focus(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            path.write_text(json.dumps({
                "feedCount": 1,
                "patCount": 0,
                "successfulFeedCount": 0,
                "totalFocusSeconds": 11,
                "focusSessions": {"old": {"seconds": 11, "completed": True}},
            }), encoding="utf-8")
            statistics = ChickenStatistics(path)
            self.assertEqual(statistics.data["focusFeedRewardsGranted"], 2)
            statistics.record_focus("old", 11, True)
            self.assertEqual(statistics.snapshot()["feedCount"], 1)
            statistics.record_focus("new", 4, False)
            self.assertEqual(statistics.snapshot()["feedCount"], 2)

    def test_existing_animation_transitions(self):
        anim = ChickenAnim()
        idle_frames = [anim.handle_event("tick")["chicken"] for _ in range(16)]
        self.assertEqual(idle_frames[3::4],
                         ["idle_01.png", "idle_02.png", "idle_03.png", "idle_00.png"])
        anim.handle_event("START_FOCUS")
        anim.handle_event("PET_CHICKEN")
        for _ in range(FRAME_COUNTS["pet"]): anim.handle_event("tick")
        self.assertEqual(anim.mood, "focused")
        anim.handle_event("PAUSE_FOCUS")
        self.assertEqual(anim.get_view()["displayKey"], 5)

    def test_common_crop_keeps_every_visible_pixel(self):
        crop = idle_crop()
        self.assertEqual(crop, {"x": 7, "y": 76, "width": 49, "height": 52})
        for mood in ("idle", "pet", "feed"):
            for path in FRAMES_DIR.glob(mood + "_*.png"):
                with Image.open(path) as image:
                    left, top, right, bottom = image.getchannel("A").getbbox()
                    self.assertGreaterEqual(left, crop["x"], path.name)
                    self.assertGreaterEqual(top, crop["y"], path.name)
                    self.assertLessEqual(right, crop["x"] + crop["width"], path.name)
                    self.assertLessEqual(bottom, crop["y"] + crop["height"], path.name)

    def test_shared_schemas_and_fixtures(self):
        root = Path(__file__).resolve().parents[2]
        for kind in ("view", "event"):
            schema = json.loads((root / f"shared/schemas/chicken-{kind}.schema.json").read_text(
                encoding="utf-8"))
            validator = Draft202012Validator(schema)
            validator.check_schema(schema)
            for fixture in (root / "shared/fixtures").glob(f"chicken-{kind}-*.json"):
                validator.validate(json.loads(fixture.read_text()))
            if kind == "view":
                view = ChickenAnim().get_view()
                view["idleCrop"] = idle_crop()
                validator.validate(view)


if __name__ == "__main__":
    unittest.main()
