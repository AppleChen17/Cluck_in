from services.llm_provider import LLMProvider
from schemas import AIRequest, AIDecision


class MessageService:

    def __init__(self, provider: LLMProvider):
        self.provider = provider

    def analyze_message(
        self,
        request: AIRequest
    ) -> AIDecision:
        return self.provider.analyze_message(request)