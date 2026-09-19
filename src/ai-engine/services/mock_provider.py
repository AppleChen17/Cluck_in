from schemas import (
    AnalyzeMessageRequest,
    AnalyzeMessageResponse,
    DraftReplyRequest,
    DraftReplyResponse,
)
from services.llm_provider import LLMProvider


class MockProvider(LLMProvider):

    def analyze_message(
        self,
        request: AnalyzeMessageRequest
    ) -> AnalyzeMessageResponse:

        return AnalyzeMessageResponse(
            priority="high",
            relevant=True,
            should_interrupt=True,
            summary=f"Mock summary: {request.content[:30]}",
            suggested_action="notify",
        )

    def draft_reply(
        self,
        request: DraftReplyRequest
    ) -> DraftReplyResponse:

        return DraftReplyResponse(
            reply="這是一則 mock reply。"
        )