"""Print mood / filename changes. Run: python src/chicken/test_states.py"""

from state_machine import ChickenAnim


def main() -> None:
    anim = ChickenAnim()
    script = [
        ("START_FOCUS", None),
        ("tick", None),
        ("PAUSE_FOCUS", None),
        ("tick", None),
        ("RESUME_FOCUS", None),
        ("PET_CHICKEN", None),
        ("tick", None),
        ("SET_MOOD", {"mood": "thinking"}),
        ("FEED_CHICKEN", None),
        ("tick", None),
        ("STOP_FOCUS", None),
    ]
    print(f"start         {anim.get_view()['mood']:10}  {anim.get_view()['chicken']}")
    for event, payload in script:
        view = anim.handle_event(event, payload)
        print(f"{event:14}  {view['mood']:10}  {view['chicken']}")


if __name__ == "__main__":
    main()
