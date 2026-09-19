from schemas import AIRequest, AIDecision
from services.llm_provider import LLMProvider


class MockProvider(LLMProvider):

    def analyze_message(
        self,
        request: AIRequest
    ) -> AIDecision:

        return AIDecision(
            messageId=request.message.id,
            decision="urgent",
            relevance=0.9,
            urgency=0.8,
            reason="Mock AI decision",
        )