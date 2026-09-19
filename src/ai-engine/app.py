from fastapi import FastAPI

from schemas import (
    AIRequest,
    AIDecision,
    TaskAnalyzeRequest,
    TaskDecision,
    MessageSummaryRequest,
    MessageSummaryResponse
)
from services.ollama_provider import OllamaProvider
from services.ai_service import AIService


app = FastAPI(
    title="Focus Chick AI Engine",
    version="0.2.0"
)

provider = OllamaProvider()
ai_service = AIService(provider)


@app.get("/health")
def health():
    return {
        "status": "ok",
        "provider": provider.__class__.__name__,
    }


@app.post(
    "/analyze-message",
    response_model=AIDecision
)
def analyze_message(
    request: AIRequest
):
    return ai_service.analyze_message(request)

@app.post(
    "/analyze-task",
    response_model=TaskDecision
)
def analyze_task(
    request: TaskAnalyzeRequest
):
    return ai_service.analyze_task(request)

@app.post(
    "/summarize-messages",
    response_model=MessageSummaryResponse
)
def summarize_messages(
    request: MessageSummaryRequest
) -> MessageSummaryResponse:
    return ai_service.summarize_messages(request)