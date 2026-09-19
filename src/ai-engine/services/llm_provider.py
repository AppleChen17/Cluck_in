from abc import ABC, abstractmethod
from schemas import (
    AnalyzeMessageRequest,
    AnalyzeMessageResponse,
    DraftReplyRequest,
    DraftReplyResponse,
)


class LLMProvider(ABC):

    @abstractmethod
    def analyze_message(
        self,
        request: AnalyzeMessageRequest
    ) -> AnalyzeMessageResponse:
        pass

    @abstractmethod
    def draft_reply(
        self,
        request: DraftReplyRequest
    ) -> DraftReplyResponse:
        pass