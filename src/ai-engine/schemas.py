from datetime import datetime
from typing import Literal, Optional

from pydantic import BaseModel, Field


class ExternalMessage(BaseModel):
    id: str = Field(min_length=1)
    source: Literal["gmail", "slack"]
    sender: str = Field(min_length=1)

    title: Optional[str] = None
    content: str

    timestamp: datetime
    unread: bool

    metadata: dict | None = None


class SessionContext(BaseModel):
    mode: Literal["idle", "focus", "auto"]
    currentTask: Optional[str]

    automationMode: Literal["on", "suggestion", "off"] = "off"

    focusStartedAt: Optional[datetime] = None
    focusDurationSeconds: int | None = Field(
        default=None,
        ge=0,
    )

    allowedApps: list[str] | None = None
    blockedApps: list[str] | None = None

    metadata: dict | None = None


class AIRequest(BaseModel):
    message: ExternalMessage
    context: SessionContext
    metadata: dict | None = None


class AIDecision(BaseModel):
    messageId: str = Field(min_length=1)

    decision: Literal[
        "urgent",
        "allow",
        "hold",
    ]

    relevance: float = Field(
        ge=0,
        le=1,
    )

    urgency: float = Field(
        ge=0,
        le=1,
    )

    requiresReply: bool
    replyDraft: str | None = None
    reason: str = Field(min_length=1)

    metadata: dict | None = None

class TaskTarget(BaseModel):
    type: Literal["app", "web"]
    name: str
    title: str | None = None
    url: str | None = None
    identifier: str | None = None


class TaskAnalyzeRequest(BaseModel):
    target: TaskTarget
    context: SessionContext


class TaskDecision(BaseModel):
    decision: Literal[
        "allow",
        "warn",
        "block",
    ]

    relevance: float = Field(
        ge=0,
        le=1,
    )

    reason: str = Field(min_length=1)

class MessageSummaryRequest(BaseModel):
    messages: list[ExternalMessage]
    context: SessionContext
    metadata: dict | None = None


class MessageSummaryResponse(BaseModel):
    messageCount: int = Field(ge=0)
    summary: str = Field(min_length=1)
    metadata: dict | None = None