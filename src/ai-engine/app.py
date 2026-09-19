from fastapi import FastAPI

from schemas import AIRequest, AIDecision
from services.ollama_provider import OllamaProvider
from services.message_service import MessageService


app = FastAPI(
    title="Focus Chick AI Engine",
    version="0.2.0"
)

provider = OllamaProvider()
message_service = MessageService(provider)


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
    return message_service.analyze_message(request)