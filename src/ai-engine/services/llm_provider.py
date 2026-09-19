from abc import ABC, abstractmethod

from schemas import (
    AIRequest,
    AIDecision,
    TaskAnalyzeRequest,
    TaskDecision,
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