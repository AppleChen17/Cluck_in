from services.llm_provider import LLMProvider
from schemas import (
    AIRequest,
    AIDecision,
    TaskAnalyzeRequest,
    TaskDecision,
)


class AIService:

    def __init__(self, provider: LLMProvider):
        self.provider = provider

    def analyze_message(
        self,
        request: AIRequest
    ) -> AIDecision:
        return self.provider.analyze_message(request)
    
    def analyze_task(
        self,
        request: TaskAnalyzeRequest
    ) -> TaskDecision:
        return self.provider.analyze_task(request)