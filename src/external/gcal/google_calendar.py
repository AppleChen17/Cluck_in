"""The real Google Calendar, over the Calendar API v3.

Why this needs OAuth when Gmail here does not: app passwords are an IMAP/SMTP
mechanism. Google Calendar has no IMAP, its CalDAV endpoint stopped accepting
basic auth years ago, and the read-only secret iCal URL cannot create anything.
For writing an event to a calendar, OAuth is the only door.

Setup is a one-off ten minutes and needs no review while the app stays in
Testing: see docs/Setup.md and scripts/setup_google_oauth.py. The one sharp edge
is that a Testing-mode refresh token expires after seven days, at which point
every call starts failing with invalid_grant and the fix is to run the setup
script again.

google-api-python-client and google-auth-oauthlib are imported lazily, the same
way slack_sdk is in the adapters, so this module imports fine without them and
nothing pays for a dependency it does not use.
"""

import logging
import threading
from datetime import datetime

import normalize
from gcal.base import CalendarBackend, CalendarError
from schemas import ExternalEvent

log = logging.getLogger("external.gcal")

# calendar.events covers reading and writing events. freebusy needs no scope of
# its own -- it is served under the same grant.
SCOPES = ["https://www.googleapis.com/auth/calendar.events"]


def _rfc3339(moment: datetime) -> str:
    """The API rejects a naive timestamp, and so does our own contract."""
    return moment.isoformat()


def to_external_event(raw: dict, calendar_id: str, tz_mode: str = "local") -> ExternalEvent | None:
    """Map one Google event resource onto the contract. None means skip it.

    All-day events are skipped rather than coerced. Google gives them as a bare
    date with no time and no offset, and external-event.schema.json requires a
    full date-time; docs/data-contracts.md explicitly defers all-day handling.
    Inventing midnight would put a fake nine-hour block on the calendar and make
    the whole day look busy.
    """
    start = (raw.get("start") or {}).get("dateTime")
    end = (raw.get("end") or {}).get("dateTime")
    if not start or not end:
        return None
    start_dt = normalize.parse_rfc3339(start)
    end_dt = normalize.parse_rfc3339(end)
    if start_dt is None or end_dt is None or end_dt <= start_dt:
        return None

    attendees: list[str] = []
    for person in raw.get("attendees") or []:
        # Display name first, address as the fallback: the contract wants
        # human-readable labels, not identity keys.
        label = (person.get("displayName") or person.get("email") or "").strip()
        if label and label not in attendees:
            attendees.append(label)

    event_id = raw.get("id") or ""
    if not event_id:
        return None

    metadata = {"calendarId": calendar_id}
    if raw.get("htmlLink"):
        metadata["htmlLink"] = raw["htmlLink"]

    return ExternalEvent(
        id="google-calendar:" + event_id,
        source="google-calendar",
        # A Google event with no summary shows as "(no title)" in the UI; the
        # contract needs a non-empty string, so say the same thing.
        title=normalize.clean_label(raw.get("summary"), "(untitled event)"),
        description=normalize.clean_title(raw.get("description")),
        startTime=normalize.to_rfc3339(start_dt, tz_mode),
        endTime=normalize.to_rfc3339(end_dt, tz_mode),
        location=normalize.clean_title(raw.get("location")),
        attendees=attendees,
        metadata=metadata,
    )


class GoogleCalendar(CalendarBackend):
    name = "google"

    def __init__(self, cfg) -> None:
        self._cfg = cfg
        self._lock = threading.Lock()
        self._service = None

    def available(self) -> bool:
        return self._cfg.token_path.exists()

    # -- auth -----------------------------------------------------------------

    def _build(self):
        """Load the stored token, refresh it if stale, and build the client."""
        from google.auth.transport.requests import Request
        from google.oauth2.credentials import Credentials
        from googleapiclient.discovery import build

        token_path = self._cfg.token_path
        if not token_path.exists():
            raise CalendarError(
                "no Google token at {}. Run: python src/external/scripts/"
                "setup_google_oauth.py".format(token_path)
            )
        creds = Credentials.from_authorized_user_file(str(token_path), SCOPES)
        if not creds.valid:
            if not (creds.expired and creds.refresh_token):
                raise CalendarError("the stored Google token cannot be refreshed; re-run setup")
            creds.refresh(Request())
            # Persist the refreshed access token, otherwise every restart spends
            # a round trip re-refreshing it.
            token_path.write_text(creds.to_json(), encoding="utf-8")
        # cache_discovery=False: the default file cache warns noisily on every
        # call outside an oauth2client setup and buys nothing here.
        return build("calendar", "v3", credentials=creds, cache_discovery=False)

    def _client(self):
        with self._lock:
            if self._service is None:
                self._service = self._build()
            return self._service

    def _call(self, request):
        try:
            return request.execute()
        except CalendarError:
            raise
        except Exception as exc:  # noqa: BLE001
            # invalid_grant is by far the most likely failure and its message is
            # opaque, so name the cause rather than making someone search it.
            hint = ""
            if "invalid_grant" in str(exc):
                hint = (
                    " -- a Testing-mode refresh token expires after 7 days; "
                    "re-run scripts/setup_google_oauth.py"
                )
            raise CalendarError("{}: {}{}".format(type(exc).__name__, exc, hint)) from exc

    # -- reads ----------------------------------------------------------------

    def busy(self, start: datetime, end: datetime) -> list[tuple[datetime, datetime]]:
        """freebusy, not events.list: one call, already merged, and it reports
        blocks from calendars whose event details we are not allowed to read."""
        body = {
            "timeMin": _rfc3339(start),
            "timeMax": _rfc3339(end),
            "items": [{"id": self._cfg.calendar_id}],
        }
        response = self._call(self._client().freebusy().query(body=body))
        entry = (response.get("calendars") or {}).get(self._cfg.calendar_id) or {}
        for error in entry.get("errors") or []:
            # Reported, never swallowed: an empty busy list and a calendar we
            # were not allowed to read look identical, and one of them would
            # have us offer a slot that is already taken.
            raise CalendarError(
                "freebusy failed for {}: {}".format(self._cfg.calendar_id, error.get("reason"))
            )
        out = []
        for period in entry.get("busy") or []:
            begins = normalize.parse_rfc3339(period.get("start", ""))
            ends = normalize.parse_rfc3339(period.get("end", ""))
            if begins is not None and ends is not None:
                out.append((begins, ends))
        return out

    def events(self, start: datetime, end: datetime) -> list[ExternalEvent]:
        response = self._call(
            self._client()
            .events()
            .list(
                calendarId=self._cfg.calendar_id,
                timeMin=_rfc3339(start),
                timeMax=_rfc3339(end),
                # Expands recurring events into their occurrences, which is what
                # "what is on my calendar" means; orderBy requires it.
                singleEvents=True,
                orderBy="startTime",
                maxResults=50,
            )
        )
        out = []
        for raw in response.get("items") or []:
            event = to_external_event(raw, self._cfg.calendar_id, self._cfg.external_tz)
            if event is not None:
                out.append(event)
        return out

    # -- writes ---------------------------------------------------------------

    def create_event(
        self,
        *,
        title: str,
        start: datetime,
        end: datetime,
        description: str | None = None,
        location: str | None = None,
        attendees: list[str] | None = None,
        metadata: dict | None = None,
    ) -> ExternalEvent:
        body: dict = {
            "summary": title,
            "start": {"dateTime": _rfc3339(start)},
            "end": {"dateTime": _rfc3339(end)},
        }
        if description:
            body["description"] = description
        if location:
            body["location"] = location
        if attendees:
            # Only entries that look like an address: Google rejects a bare
            # display name here, and the contract allows either spelling.
            invited = [{"email": a} for a in attendees if "@" in a]
            if invited:
                body["attendees"] = invited
        response = self._call(
            self._client()
            .events()
            .insert(
                calendarId=self._cfg.calendar_id,
                body=body,
                # "none" unless explicitly enabled: an invitation email leaves
                # the machine, which is the line EXTERNAL_SEND_DRY_RUN guards
                # for messages.
                sendUpdates="all" if self._cfg.calendar_send_invites else "none",
            )
        )
        event = to_external_event(response, self._cfg.calendar_id, self._cfg.external_tz)
        if event is None:
            raise CalendarError("Google accepted the event but returned something unusable")
        if metadata:
            event = event.model_copy(update={"metadata": {**(event.metadata or {}), **metadata}})
        log.info("created calendar event %s (%s)", event.id, event.startTime)
        return event
