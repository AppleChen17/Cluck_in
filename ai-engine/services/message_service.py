from services.llm_provider import LLMProvider
from schemas import (
    AnalyzeMessageRequest,
    AnalyzeMessageResponse,
    DraftReplyRequest,
    DraftReplyResponse,
)


class MessageService:

    def __init__(self, provider: LLMProvider):
        self.provider = provider

    def analyze_message(
        self,
        request: AnalyzeMessageRequest
    ) -> AnalyzeMessageResponse:
        return self.provider.analyze_message(request)

    def draft_reply(
        self,
        request: DraftReplyRequest
    ) -> DraftReplyResponse:
        return self.provider.draft_reply(request)