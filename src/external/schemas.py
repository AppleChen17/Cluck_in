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


class HealthResponse(BaseModel):
    status: Literal["ok", "degraded"]
    epoch: str
    buffered: int
    adapters: list[AdapterStatus]


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
