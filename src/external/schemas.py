"""Contract models for the external module.

ExternalMessage mirrors shared/schemas/external-message.schema.json exactly.
The attribute names ARE the wire names — do not rename them to please a linter.

Two schema details drive the custom serializer below:
  * `metadata` is declared `"type": "object"` and does NOT accept null, so a
    None metadata must be omitted from the payload entirely.
  * `title` IS declared nullable and must survive as an explicit null, so a
    blanket exclude_none would be wrong.
"""

from typing import Annotated, Any, Literal

from pydantic import BaseModel, ConfigDict, Field, model_serializer

NonEmptyStr = Annotated[str, Field(min_length=1)]


class ExternalMessage(BaseModel):
    """A normalized message from Gmail or Slack."""

    model_config = ConfigDict(extra="forbid")

    id: NonEmptyStr
    source: Literal["gmail", "slack"]
    sender: NonEmptyStr
    title: NonEmptyStr | None = None
    content: str
    timestamp: str
    unread: bool
    metadata: dict[str, Any] | None = None

    @model_serializer
    def _to_wire(self) -> dict[str, Any]:
        data: dict[str, Any] = {
            "id": self.id,
            "source": self.source,
            "sender": self.sender,
            "title": self.title,
            "content": self.content,
            "timestamp": self.timestamp,
            "unread": self.unread,
        }
        if self.metadata is not None:
            data["metadata"] = self.metadata
        return data


class AdapterStatus(BaseModel):
    """Per-adapter health, reported by GET /health."""

    name: str
    enabled: bool
    connected: bool
    lastEventAt: str | None = None
    lastError: str | None = None
    emitted: int = 0


class SendingStatus(BaseModel):
    """The outbound half, reported by GET /health.

    `dryRun` is the field to look at first. Everything else can be perfectly
    configured and nothing will leave the machine while it is true -- which is
    the intent, and also the most likely explanation for "the reply never
    arrived".
    """

    dryRun: bool
    allowlisted: int
    ready: list[str] = []
    replyTargets: int = 0
    outbox: int = 0
    calendarEvents: int = 0


class CalendarStatus(BaseModel):
    """`backend` is what is actually in use, which is not always what was asked
    for: CALENDAR_BACKEND=google with no token falls back to memory and says so
    here rather than failing at startup."""

    backend: str
    available: bool


class HealthResponse(BaseModel):
    status: Literal["ok", "degraded"]
    epoch: str
    buffered: int
    adapters: list[AdapterStatus]
    sending: SendingStatus | None = None
    calendar: CalendarStatus | None = None


class MessagesResponse(BaseModel):
    """Envelope for GET /messages.

    The envelope itself is NOT a shared contract; only the items in `messages`
    are. Nothing may be added to an item — external-message.schema.json is
    additionalProperties:false, so a stray helper field breaks strict consumers.
    The sequence number therefore lives here and in the cursor, never in a message.
    """

    messages: list[ExternalMessage]
    cursor: str
    count: int
    hasMore: bool
    replayed: bool = False


class ExternalEvent(BaseModel):
    """A normalized calendar event.

    Mirrors shared/schemas/external-event.schema.json. The external module is
    that contract's declared producer in docs/data-contracts.md; until now
    nothing produced one.

    Nullable versus omitted is a real distinction here, and the two optional
    fields fall on opposite sides of it:
      * `description` and `location` are emitted as an explicit null when the
        provider says there is none -- we know, so "not supplied" would be a lie.
      * `attendees` is omitted only when the backend cannot tell us; an event
        with nobody invited emits [], which the contract defines as "no
        attendees" rather than "unknown".
    """

    model_config = ConfigDict(extra="forbid")

    id: NonEmptyStr
    source: Literal["google-calendar"]
    title: NonEmptyStr
    description: str | None = None
    startTime: str
    endTime: str
    location: NonEmptyStr | None = None
    attendees: list[NonEmptyStr] | None = None
    metadata: dict[str, Any] | None = None

    @model_serializer
    def _to_wire(self) -> dict[str, Any]:
        data: dict[str, Any] = {
            "id": self.id,
            "source": self.source,
            "title": self.title,
            "description": self.description,
            "startTime": self.startTime,
            "endTime": self.endTime,
            "location": self.location,
        }
        if self.attendees is not None:
            data["attendees"] = self.attendees
        if self.metadata is not None:
            data["metadata"] = self.metadata
        return data


# -- sending ------------------------------------------------------------------


class ReplyRequest(BaseModel):
    """Reply to a message this service has already delivered.

    Routing is looked up from `messageId`, so the caller never has to know the
    sender's email address, the RFC 822 Message-ID, or the Slack channel and
    thread. Those live outside the ExternalMessage contract on purpose -- see
    reply_registry.py.
    """

    model_config = ConfigDict(extra="forbid")

    messageId: NonEmptyStr
    body: NonEmptyStr
    # Gmail only. None derives "Re: <original subject>".
    subject: str | None = None
    # Slack only. True (the default) keeps the reply in the thread rather than
    # posting it loose in the channel.
    inThread: bool = True


class GmailSendRequest(BaseModel):
    """Send a fresh email that is not a reply to anything."""

    model_config = ConfigDict(extra="forbid")

    to: list[NonEmptyStr] = Field(min_length=1)
    subject: NonEmptyStr
    body: NonEmptyStr
    cc: list[NonEmptyStr] | None = None


class SlackSendRequest(BaseModel):
    """Post to a Slack channel. Empty channel uses SLACK_DEFAULT_CHANNEL."""

    model_config = ConfigDict(extra="forbid")

    channel: str | None = None
    text: NonEmptyStr
    threadTs: str | None = None


class ReactRequest(BaseModel):
    """Add an emoji reaction to a message. Slack only."""

    model_config = ConfigDict(extra="forbid")

    messageId: NonEmptyStr
    # Either a Slack short name ("thumbsup", ":thumbsup:") or the emoji itself
    # ("👍"), which is translated for you -- see sender/slack_sender.py.
    emoji: str = "thumbsup"


class SendResult(BaseModel):
    """What happened to one send. Always 200; read the fields.

    `delivered` and `dryRun` are separate because a dry run is a success: the
    request was valid and fully routed, it simply was not handed to the
    provider. Callers branch on `delivered`, never on the HTTP status.
    """

    id: str
    source: Literal["gmail", "slack"]
    target: str
    delivered: bool
    dryRun: bool
    duplicate: bool = False
    inReplyTo: str | None = None
    providerId: str | None = None
    preview: str
    timestamp: str
    error: str | None = None


class OutboxResponse(BaseModel):
    sent: list[SendResult]
    count: int


# -- calendar -----------------------------------------------------------------


class SlotModel(BaseModel):
    start: str
    end: str


class AvailabilityRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    durationMinutes: int = Field(default=30, ge=5, le=480)
    withinDays: int = Field(default=5, ge=1, le=30)
    limit: int = Field(default=5, ge=1, le=20)


class AvailabilityResponse(BaseModel):
    """Candidate free slots, plus a ready-to-paste rendering of them.

    `text` exists because date arithmetic is exactly what a small local model
    gets wrong. The model should quote this string, not compute it. It is
    rendered in Traditional Chinese to match the messages this module sees.
    """

    slots: list[SlotModel]
    text: str
    durationMinutes: int
    searchedFrom: str
    searchedTo: str
    backend: str


class CreateEventRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    title: NonEmptyStr
    startTime: str
    endTime: str
    description: str | None = None
    location: str | None = None
    attendees: list[NonEmptyStr] | None = None
    # Set when the event was created in response to a message, so the dashboard
    # can show what caused it. Stored in the event's metadata, never sent to
    # Google.
    fromMessageId: str | None = None


class EventsResponse(BaseModel):
    """Envelope for the calendar endpoints. Only `events` is contract data.

    `duplicate` is true when a create was answered from the idempotency memo
    rather than by creating anything: the same `fromMessageId` had already put
    an event on the calendar. The event returned is the original one.
    """

    events: list[ExternalEvent]
    count: int
    backend: str
    duplicate: bool = False
