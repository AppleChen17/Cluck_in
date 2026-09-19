from fastapi import FastAPI

from schemas import (
    AnalyzeMessageRequest,
    AnalyzeMessageResponse,
    DraftReplyRequest,
    DraftReplyResponse,
)

from services.mock_provider import MockProvider
from services.message_service import MessageService


app = FastAPI(
    title="Focus Chick AI Engine",
    version="0.1.0"
)

provider = MockProvider()
message_service = MessageService(provider)


@app.get("/health")
def health():
    return {
        "status": "ok",
        "provider": provider.__class__.__name__,
    }


@app.post(
    "/analyze-message",
    response_model=AnalyzeMessageResponse
)
def analyze_message(
    request: AnalyzeMessageRequest
):
    return message_service.analyze_message(request)


@app.post(
    "/draft-reply",
    response_model=DraftReplyResponse
)
def draft_reply(
    request: DraftReplyRequest
):
    return message_service.draft_reply(request)