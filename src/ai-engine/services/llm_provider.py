from abc import ABC, abstractmethod

from schemas import (
    AIRequest,
    AIDecision,
    TaskAnalyzeRequest,
    TaskDecision,
    MessageSummaryRequest,
    MessageSummaryResponse
)


class LLMProvider(ABC):

    @abstractmethod
    def analyze_message(
        self,
        request: AIRequest
    ) -> AIDecision:
        pass

    @abstractmethod
    def analyze_task(
        self,
        request: TaskAnalyzeRequest
    ) -> TaskDecision:
        pass

    @abstractmethod
    def summarize_messages(
        self,
        request: MessageSummaryRequest
    ) -> MessageSummaryResponse:
        pass