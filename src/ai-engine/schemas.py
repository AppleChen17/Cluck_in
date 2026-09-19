from typing import Literal
from pydantic import BaseModel


class AnalyzeMessageRequest(BaseModel):
    current_task: str
    source: Literal["gmail", "slack", "other"]
    sender: str
    content: str


class AnalyzeMessageResponse(BaseModel):
    priority: Literal["low", "medium", "high"]
    relevant: bool
    should_interrupt: bool
    summary: str
    suggested_action: Literal[
        "ignore",
        "notify",
        "draft_reply",
        "reply_immediately"
    ]


class DraftReplyRequest(BaseModel):
    content: str
    context: str | None = None


class DraftReplyResponse(BaseModel):
    reply: str