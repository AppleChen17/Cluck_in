"""One-off: authorize this machine against your Google Calendar.

Run it once, click through the browser, and a token lands next to config.py.
After that CALENDAR_BACKEND=google works and nothing asks you again -- until the
seven-day expiry described at the bottom of this docstring.

    .\\.venv\\Scripts\\python.exe src\\external\\scripts\\setup_google_oauth.py

Before running it, in https://console.cloud.google.com (about ten minutes, once):

  1. Create a project. Any name.
  2. APIs & Services -> Library -> enable "Google Calendar API".
  3. APIs & Services -> OAuth consent screen: User Type "External", fill in the
     three required fields, and add your own Gmail address under "Test users".
     LEAVE IT IN "Testing". Do not submit it for verification -- the review
     takes weeks and buys nothing for a demo. PRODUCT_SPEC_MVP.md section 12
     says the same thing about the Gmail side.
  4. Credentials -> Create credentials -> OAuth client ID -> "Desktop app".
     Download the JSON and save it as src/external/credentials.json.
  5. Run this script. The browser will warn "Google hasn't verified this app";
     that is what "Testing" means. Advanced -> Continue.

Then set CALENDAR_BACKEND=google in src/external/.env.

The seven-day expiry: while the consent screen stays in Testing, Google expires
refresh tokens after seven days. One morning every calendar call starts failing
with invalid_grant. That is not a bug and nothing is broken -- run this script
again. For a hackathon that trade is strictly better than the verification queue.

This script is the only thing in the module that opens a browser, and nothing
imports it.
"""

import sys
from pathlib import Path

MODULE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(MODULE_ROOT))

# A Windows console defaults to a legacy code page, which cannot encode every
# character a Google error message might contain. Never let output kill a run.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

from config import load_settings  # noqa: E402
from gcal.google_calendar import SCOPES  # noqa: E402


def main() -> int:
    try:
        from google_auth_oauthlib.flow import InstalledAppFlow
    except ImportError:
        print(
            "google-auth-oauthlib is not installed. From the repo root:\n"
            "  .\\.venv\\Scripts\\python.exe -m pip install -r src\\external\\requirements.txt"
        )
        return 1

    cfg = load_settings()
    credentials_path = cfg.credentials_path
    token_path = cfg.token_path

    if not credentials_path.exists():
        print("No OAuth client file at:\n  {}\n".format(credentials_path))
        print("Create one at https://console.cloud.google.com:")
        print("  Credentials -> Create credentials -> OAuth client ID -> Desktop app")
        print("Then save the downloaded JSON to the path above.")
        print("\nThe full walkthrough is in the docstring at the top of this file.")
        return 1

    if token_path.exists():
        # Overwriting is almost always what someone running this wants (the
        # usual reason to run it twice is an expired token), but say so rather
        # than silently discarding a working credential.
        print("A token already exists at {}".format(token_path))
        answer = input("Replace it? [y/N] ").strip().lower()
        if answer not in ("y", "yes"):
            print("Left alone. Nothing changed.")
            return 0

    print("Opening a browser. Sign in as the account whose calendar this is.")
    print("Expect a \"Google hasn't verified this app\" warning: Advanced -> Continue.\n")
    flow = InstalledAppFlow.from_client_secrets_file(str(credentials_path), SCOPES)
    # port=0 takes any free port. A fixed port collides with whatever else is
    # listening and fails with an error that says nothing about ports.
    creds = flow.run_local_server(port=0, prompt="consent")

    token_path.write_text(creds.to_json(), encoding="utf-8")
    print("\nToken written to {}".format(token_path))
    print("It is gitignored. It is also a credential -- do not paste it anywhere.")

    print("\nChecking the calendar answers...")
    try:
        from datetime import datetime, timedelta, timezone

        from gcal.google_calendar import GoogleCalendar

        now = datetime.now(timezone.utc)
        events = GoogleCalendar(cfg).events(now, now + timedelta(days=7))
        print("OK: {} timed event(s) in the next 7 days.".format(len(events)))
        for event in events[:5]:
            print("  {}  {}".format(event.startTime, event.title))
    except Exception as exc:  # noqa: BLE001
        print("The token was saved but the test call failed: {}".format(exc))
        print("Check that the Google Calendar API is enabled for this project.")
        return 1

    print("\nNow set CALENDAR_BACKEND=google in src/external/.env and restart the service.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
