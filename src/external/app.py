"""HTTP surface of the external module.

Inbound, this module fetches and normalizes messages. It does NOT call the AI
engine -- the C# app polls GET /messages here, composes its own AIRequest, and
calls src/ai-engine separately. Keep it that way.

Outbound, it sends what the app decides to say: POST /reply, /send/gmail,
/send/slack, /react, and the /calendar endpoints. The same rule applies in this
direction -- nothing here decides WHAT to reply, only how to deliver it. The
decision belongs to whoever called.

Sending is off by default (EXTERNAL_SEND_DRY_RUN=true). A dry run validates,
routes and records everything and skips only the provider call, so the whole
integration can be built and rehearsed before anything leaves the machine.

IMPORTANT: run with --workers 1 and without --reload. Multiple workers are
separate processes, which means separate in-memory buffers (the client would
see messages appear and vanish depending on which worker answered), separate
reply registries (POST /reply would 404 depending on which worker answered),
and two Slack socket connections delivering every event twice.
"""

import logging
from contextlib import asynccontextmanager
from datetime import datetime, timedelta, timezone

from fastapi import FastAPI, HTTPException, Query

import normalize
from buffer import MessageBuffer, encode_cursor, parse_cursor
from config import ConfigError, Settings, load_settings
from gcal.base import CalendarBackend, CalendarError
from idempotency import OnceByKey
from reply_registry import ReplyRegistry, target_from_message
from schemas import (
    CalendarStatus,
    CreateEventRequest,
    EventsResponse,
    ExternalMessage,
    GmailSendRequest,
    HealthResponse,
    MessagesResponse,
    OutboxResponse,
    ReactRequest,
    ReplyRequest,
    SendResult,
    SendingStatus,
    SlackSendRequest,
)
from sender.base import Dispatcher, SendBlocked, SendUnavailable
from sender.gmail_sender import GmailSender, build_references, build_reply_subject
from sender.outbox import Outbox
from sender.slack_sender import SlackSender

# uvicorn configures only its own "uvicorn.*" loggers, and they do not
# propagate. Without this the root logger has no handler, logging falls back to
# lastResort at WARNING, and every log.info below -- "Message source: slack"
# included -- is silently discarded. basicConfig only installs a handler when
# the root has none, so it never duplicates uvicorn's own output.
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
)
log = logging.getLogger("external")

settings: Settings = load_settings()
buffer = MessageBuffer(maxlen=settings.external_buffer_size)
replies = ReplyRegistry(maxlen=settings.external_reply_registry_size)
outbox = Outbox(maxlen=settings.external_outbox_size)
# One calendar entry per incoming message, for the same reason there is
# one reply per incoming message -- and with a longer tail, because a
# duplicate meeting is still on the calendar tomorrow.
created_events = OnceByKey(maxlen=settings.external_outbox_size)
adapters: list = []
dispatcher: Dispatcher | None = None
calendar: CalendarBackend | None = None


def _build_adapters(cfg: Settings) -> list:
    """Instantiate the sources EXTERNAL_ADAPTERS names.

    Call validate_sources() first: this assumes every name is known and every
    selected source has its credentials.
    """
    from adapters.fixture_adapter import FixtureAdapter

    built = []
    for name in cfg.adapters:
        if name == "fixture":
            built.append(FixtureAdapter())
        elif name == "gmail":
            from adapters.gmail_adapter import GmailAdapter

            built.append(GmailAdapter(cfg))
        elif name == "slack":
            from adapters.slack_adapter import SlackAdapter

            built.append(SlackAdapter(cfg))
        else:  # pragma: no cover - validate_sources rejects these first
            raise ConfigError("unknown message source {!r} in EXTERNAL_ADAPTERS".format(name))
    return built


def _announce_source(cfg: Settings) -> None:
    """One unmissable line saying where messages are about to come from.

    "Message source: fixture" when a demo shows Bob and Alice is the difference
    between a five second fix and half an hour spent looking for the Slack bug
    that is not there.
    """
    names = cfg.adapters
    log.info("Message source: %s", ", ".join(names))
    if "fixture" in names and len(names) > 1:
        # Not an error -- seeding a live dashboard with the example messages is
        # a legitimate rehearsal trick -- but it must never be a surprise.
        real = [n for n in names if n != "fixture"]
        log.warning(
            "EXTERNAL_ADAPTERS mixes 'fixture' with %s: the committed Bob/Alice "
            "example messages will appear in GET /messages alongside real ones. "
            "Drop 'fixture' for a clean run.",
            ", ".join(real),
        )
    for source in ("slack", "gmail"):
        if getattr(cfg, "{}_enabled".format(source)) and source not in names:
            # These two flags are documented but read by nothing. Setting
            # SLACK_ENABLED=true and expecting Slack messages is the easiest
            # possible mistake to make, and it fails as fixture data.
            log.warning(
                "%s_ENABLED=true but EXTERNAL_ADAPTERS=%s does not include '%s'. "
                "EXTERNAL_ADAPTERS is the only switch that selects sources; "
                "%s_ENABLED is ignored.",
                source.upper(),
                cfg.external_adapters.strip(),
                source,
                source.upper(),
            )


def _build_calendar(cfg: Settings) -> CalendarBackend:
    """The Google backend when asked for, the in-process one otherwise.

    Falling back rather than failing is deliberate and matches the fixture
    adapter: a missing token should cost you real calendar data, not the ability
    to run the service at all.
    """
    if cfg.calendar_backend.strip().lower() == "google":
        from gcal.google_calendar import GoogleCalendar

        backend = GoogleCalendar(cfg)
        if backend.available():
            return backend
        log.warning(
            "CALENDAR_BACKEND=google but no token at %s; falling back to the "
            "in-memory calendar. Run scripts/setup_google_oauth.py to fix this.",
            cfg.token_path,
        )
    from gcal.memory_calendar import MemoryCalendar

    return MemoryCalendar(tz_mode=cfg.external_tz)


@asynccontextmanager
async def lifespan(_: FastAPI):
    global dispatcher, calendar

    # Before anything else, and fatal on purpose. A service that starts on a
    # broken EXTERNAL_ADAPTERS goes on answering GET /messages with 200 and
    # either nothing or the fixtures, and every consumer downstream reads that
    # as "no messages arrived" rather than "this was never configured".
    try:
        settings.validate_sources()
    except ConfigError as exc:
        log.error("Message source configuration error:\n%s", exc)
        raise
    _announce_source(settings)

    dispatcher = Dispatcher(
        settings,
        outbox,
        {"gmail": GmailSender(settings), "slack": SlackSender(settings)},
    )
    calendar = _build_calendar(settings)

    adapters.extend(_build_adapters(settings))
    for adapter in adapters:
        adapter.bind_reply_registry(replies)
        # One misconfigured adapter (a bad Slack token, say) must never stop the
        # other from serving, so each start is isolated.
        try:
            adapter.start(buffer.add)
            log.info("adapter %s started", adapter.name)
        except Exception as exc:  # noqa: BLE001
            adapter._mark_error(f"{type(exc).__name__}: {exc}")
            log.exception("adapter %s failed to start", adapter.name)

    # Config is already known good by here, so anything that failed failed for a
    # live reason -- a revoked token, no network, Slack down. The service keeps
    # serving (GET /health reports it as degraded with the error), but losing
    # every real source must not be a single line in the middle of the log.
    live = [a for a in adapters if a.name != "fixture"]
    if live and all(a.health().lastError for a in live):
        log.error(
            "No message source is connected: %s. GET /messages will stay empty "
            "until this is fixed; GET /health carries the error.",
            "; ".join("{}: {}".format(a.name, a.health().lastError) for a in live),
        )
    yield
    for adapter in adapters:
        try:
            adapter.stop()
        except Exception:  # noqa: BLE001
            log.exception("adapter %s failed to stop", adapter.name)


app = FastAPI(
    title="Cluck In External",
    version="0.2.0",
    description=(
        "Normalizes Gmail and Slack messages into the ExternalMessage contract, "
        "sends replies back to them, and reads and writes Google Calendar."
    ),
    lifespan=lifespan,
)


def _now() -> datetime:
    """Now, with the offset EXTERNAL_TZ asks for. Never naive."""
    moment = datetime.now(timezone.utc)
    return moment if settings.external_tz == "utc" else moment.astimezone()


def _dispatcher() -> Dispatcher:
    if dispatcher is None:  # pragma: no cover - only reachable outside lifespan
        raise HTTPException(status_code=503, detail="the service is still starting")
    return dispatcher


def _calendar() -> CalendarBackend:
    if calendar is None:  # pragma: no cover - only reachable outside lifespan
        raise HTTPException(status_code=503, detail="the service is still starting")
    return calendar


@app.get("/health", response_model=HealthResponse)
def health() -> HealthResponse:
    """Liveness plus per-adapter status. Never performs network I/O."""
    statuses = [a.health() for a in adapters]
    degraded = any(s.lastError for s in statuses) or not statuses
    ready = dispatcher.ready() if dispatcher is not None else []
    return HealthResponse(
        status="degraded" if degraded else "ok",
        epoch=buffer.epoch,
        buffered=buffer.stats()["buffered"],
        adapters=statuses,
        sending=SendingStatus(
            dryRun=settings.external_send_dry_run,
            allowlisted=len(settings.send_allowlist),
            ready=ready,
            replyTargets=len(replies),
            outbox=outbox.stats()["recorded"],
            calendarEvents=len(created_events),
        ),
        calendar=CalendarStatus(
            backend=calendar.name if calendar is not None else "none",
            available=calendar.available() if calendar is not None else False,
        ),
    )


# -- inbound ------------------------------------------------------------------


@app.get("/messages", response_model=MessagesResponse)
def messages(
    cursor: str | None = Query(
        default=None,
        description="Opaque value from a previous response. Store and echo it verbatim; "
        "do not parse it and do not use a timestamp as a cursor.",
    ),
    limit: int = Query(default=100, ge=1, le=500),
    since: str | None = Query(
        default=None,
        description="Optional EXTRA filter on the send timestamp (RFC 3339, with "
        "an explicit offset). This is not a delivery cursor.",
    ),
) -> MessagesResponse:
    epoch, seq = parse_cursor(cursor)
    # A cursor from a previous process refers to sequence numbers that no longer
    # mean anything. Replay the buffer rather than returning an empty list
    # forever, which is how a naive integer cursor fails silently for hours.
    replayed = bool(cursor) and epoch != buffer.epoch
    if epoch != buffer.epoch:
        seq = 0

    try:
        items, last_seq, has_more = buffer.read_after(seq, limit, since)
    except ValueError as exc:
        # `since` is the client's, so this is a 422 like any other bad query
        # parameter -- not a 500, and never a silently ignored filter.
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    return MessagesResponse(
        messages=items,
        cursor=encode_cursor(buffer.epoch, last_seq),
        count=len(items),
        hasMore=has_more,
        replayed=replayed,
    )


@app.post("/debug/inject", response_model=MessagesResponse, include_in_schema=False)
def debug_inject(message: ExternalMessage) -> MessagesResponse:
    """Push a hand-written message into the buffer. Gated by EXTERNAL_DEBUG."""
    if not settings.external_debug:
        raise HTTPException(status_code=404, detail="not found")
    accepted = buffer.add(message)
    # Registered so an injected message can be replied to like a real one. For
    # gmail that needs metadata.replyToAddress; see reply_registry.
    target = target_from_message(message)
    if target is not None:
        replies.remember(target)
    return MessagesResponse(
        messages=[message] if accepted else [],
        cursor=encode_cursor(buffer.epoch, buffer.latest_seq()),
        count=1 if accepted else 0,
        hasMore=False,
    )


# -- outbound -----------------------------------------------------------------


def _send_error(exc: Exception) -> HTTPException:
    if isinstance(exc, SendBlocked):
        return HTTPException(status_code=403, detail=str(exc))
    return HTTPException(status_code=503, detail=str(exc))


@app.post("/reply", response_model=SendResult)
def reply(request: ReplyRequest) -> SendResult:
    """Reply to a message this service delivered, routed by its id.

    At most one reply per message, ever. The Gmail adapter re-fetches the same
    unread mail on every poll and a restarted caller replays its cursor, so
    without this one incoming email would become a reply every thirty seconds.
    A repeat returns the original result with duplicate=true and sends nothing.
    """
    previous = outbox.already_replied(request.messageId)
    if previous is not None:
        return previous

    target = replies.get(request.messageId)
    if target is None:
        raise HTTPException(
            status_code=404,
            detail=(
                "no reply route known for {!r}. The registry is in memory, so a "
                "message from before the last restart cannot be answered by id; "
                "use POST /send/gmail or /send/slack with an explicit "
                "destination instead.".format(request.messageId)
            ),
        )

    try:
        if target.source == "gmail":
            return _dispatcher().dispatch(
                source="gmail",
                destination=target.address or "",
                body=request.body,
                reply_to_message_id=request.messageId,
                subject=request.subject or build_reply_subject(target.subject),
                in_reply_to=target.rfc_message_id,
                references=build_references(target.references, target.rfc_message_id),
            )
        return _dispatcher().dispatch(
            source="slack",
            destination=target.channel or "",
            body=request.body,
            reply_to_message_id=request.messageId,
            # None posts it loose in the channel instead of under the message.
            thread_ts=target.thread_ts if request.inThread else None,
        )
    except (SendBlocked, SendUnavailable) as exc:
        raise _send_error(exc) from exc


@app.post("/send/gmail", response_model=SendResult)
def send_gmail(request: GmailSendRequest) -> SendResult:
    """Send a fresh email. Not deduplicated -- it answers no message."""
    try:
        return _dispatcher().dispatch(
            source="gmail",
            destination=request.to[0],
            body=request.body,
            # Every recipient is allowlist-checked, not just the first: an
            # allowlist that looks only at to[0] can be walked straight past.
            also_check=[*request.to[1:], *(request.cc or [])],
            subject=request.subject,
            to=request.to,
            cc=request.cc,
        )
    except (SendBlocked, SendUnavailable) as exc:
        raise _send_error(exc) from exc


@app.post("/send/slack", response_model=SendResult)
def send_slack(request: SlackSendRequest) -> SendResult:
    """Post to a channel. Not deduplicated -- it answers no message."""
    channel = (request.channel or settings.slack_default_channel).strip()
    if not channel:
        raise HTTPException(
            status_code=422,
            detail="no channel given and SLACK_DEFAULT_CHANNEL is empty",
        )
    try:
        return _dispatcher().dispatch(
            source="slack",
            destination=channel,
            body=request.text,
            thread_ts=request.threadTs,
        )
    except (SendBlocked, SendUnavailable) as exc:
        raise _send_error(exc) from exc


@app.post("/react")
def react(request: ReactRequest) -> dict:
    """Add an emoji reaction to a Slack message.

    Acknowledging a meeting time with a thumbs up is a real answer and a much
    cheaper one than a sentence, so it gets its own endpoint rather than being
    squeezed into /reply. Gmail has no equivalent and says so.
    """
    target = replies.get(request.messageId)
    if target is None:
        raise HTTPException(
            status_code=404, detail="no route known for {!r}".format(request.messageId)
        )
    if target.source != "slack":
        raise HTTPException(
            status_code=422,
            detail="reactions are a Slack feature; {} has none".format(target.source),
        )
    if settings.external_send_dry_run:
        return {
            "messageId": request.messageId,
            "emoji": request.emoji,
            "delivered": False,
            "dryRun": True,
        }
    sender = _dispatcher().sender_for("slack")
    try:
        _dispatcher().check_destination(target.channel or "")
        name = sender.react(channel=target.channel, ts=target.ts, emoji=request.emoji)
    except (SendBlocked, SendUnavailable) as exc:
        raise _send_error(exc) from exc
    except Exception as exc:  # noqa: BLE001
        raise HTTPException(status_code=502, detail="{}: {}".format(type(exc).__name__, exc))
    return {"messageId": request.messageId, "emoji": name, "delivered": True, "dryRun": False}


@app.get("/outbox", response_model=OutboxResponse)
def get_outbox(limit: int = Query(default=50, ge=1, le=500)) -> OutboxResponse:
    """What was sent, newest first. Includes dry runs, which is the point."""
    records = outbox.recent(limit)
    return OutboxResponse(sent=records, count=len(records))


# -- calendar -----------------------------------------------------------------


@app.get("/calendar/events", response_model=EventsResponse)
def list_events(withinDays: int = Query(default=7, ge=1, le=60)) -> EventsResponse:
    """Upcoming events as the ExternalEvent contract, oldest first."""
    try:
        events = _calendar().events(_now(), _now() + timedelta(days=withinDays))
    except CalendarError as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
    return EventsResponse(events=events, count=len(events), backend=_calendar().name)


@app.post("/calendar/events", response_model=EventsResponse, status_code=201)
def create_event(request: CreateEventRequest) -> EventsResponse:
    """Put a meeting on the calendar.

    Times are validated here rather than at the provider: the contract requires
    an explicit offset and an end strictly after the start, and a 422 naming
    which one is wrong beats a 400 from Google that does not.
    """
    start = normalize.parse_rfc3339(request.startTime)
    end = normalize.parse_rfc3339(request.endTime)
    if start is None or end is None:
        raise HTTPException(
            status_code=422,
            detail="startTime and endTime must be RFC 3339 with an explicit offset",
        )
    if end <= start:
        raise HTTPException(status_code=422, detail="endTime must be later than startTime")

    if request.fromMessageId:
        previous = created_events.get(request.fromMessageId)
        if previous is not None:
            # Answered from the memo. Creating a second identical meeting
            # because a caller replayed its cursor is the kind of mistake that
            # is still visible on someone's calendar a week later.
            return EventsResponse(
                events=[previous], count=1, backend=_calendar().name, duplicate=True
            )

    metadata = {"fromMessageId": request.fromMessageId} if request.fromMessageId else None
    try:
        event = _calendar().create_event(
            title=request.title,
            start=start,
            end=end,
            description=request.description,
            location=request.location,
            attendees=request.attendees,
            metadata=metadata,
        )
    except CalendarError as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
    if request.fromMessageId:
        created_events.remember(request.fromMessageId, event)
    return EventsResponse(events=[event], count=1, backend=_calendar().name)
