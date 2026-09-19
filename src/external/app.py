"""HTTP surface of the external module.

This module fetches and normalizes messages. It does NOT call the AI engine —
the C# app polls GET /messages here, composes its own AIRequest, and calls
src/ai-engine separately. Keep it that way.

IMPORTANT: run with --workers 1 and without --reload. Multiple workers are
separate processes, which means separate in-memory buffers (the client would
see messages appear and vanish depending on which worker answered) and two
Slack socket connections delivering every event twice.
"""

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, Query

from buffer import MessageBuffer, encode_cursor, parse_cursor
from config import Settings, load_settings
from schemas import ExternalMessage, HealthResponse, MessagesResponse

log = logging.getLogger("external")

settings: Settings = load_settings()
buffer = MessageBuffer(maxlen=settings.external_buffer_size)
adapters: list = []


def _build_adapters(cfg: Settings) -> list:
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
        else:
            log.warning("unknown adapter %r in EXTERNAL_ADAPTERS, ignoring", name)
    return built


@asynccontextmanager
async def lifespan(_: FastAPI):
    adapters.extend(_build_adapters(settings))
    for adapter in adapters:
        # One misconfigured adapter (a bad Slack token, say) must never stop the
        # other from serving, so each start is isolated.
        try:
            adapter.start(buffer.add)
            log.info("adapter %s started", adapter.name)
        except Exception as exc:  # noqa: BLE001
            adapter._mark_error(f"{type(exc).__name__}: {exc}")
            log.exception("adapter %s failed to start", adapter.name)
    yield
    for adapter in adapters:
        try:
            adapter.stop()
        except Exception:  # noqa: BLE001
            log.exception("adapter %s failed to stop", adapter.name)


app = FastAPI(
    title="Cluck In External",
    version="0.1.0",
    description="Normalizes Gmail and Slack messages into the ExternalMessage contract.",
    lifespan=lifespan,
)


@app.get("/health", response_model=HealthResponse)
def health() -> HealthResponse:
    """Liveness plus per-adapter status. Never performs network I/O."""
    statuses = [a.health() for a in adapters]
    degraded = any(s.lastError for s in statuses) or not statuses
    return HealthResponse(
        status="degraded" if degraded else "ok",
        epoch=buffer.epoch,
        buffered=buffer.stats()["buffered"],
        adapters=statuses,
    )


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
        description="Optional EXTRA filter on the send timestamp (RFC 3339). "
        "This is not a delivery cursor.",
    ),
) -> MessagesResponse:
    epoch, seq = parse_cursor(cursor)
    # A cursor from a previous process refers to sequence numbers that no longer
    # mean anything. Replay the buffer rather than returning an empty list
    # forever, which is how a naive integer cursor fails silently for hours.
    replayed = bool(cursor) and epoch != buffer.epoch
    if epoch != buffer.epoch:
        seq = 0

    items, last_seq, has_more = buffer.read_after(seq, limit, since)
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
    from fastapi import HTTPException

    if not settings.external_debug:
        raise HTTPException(status_code=404, detail="not found")
    accepted = buffer.add(message)
    return MessagesResponse(
        messages=[message] if accepted else [],
        cursor=encode_cursor(buffer.epoch, buffer.latest_seq()),
        count=1 if accepted else 0,
        hasMore=False,
    )
