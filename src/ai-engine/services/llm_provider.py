from abc import ABC, abstractmethod

from schemas import AIRequest, AIDecision


class LLMProvider(ABC):

    @abstractmethod
    def analyze_message(
        self,
        request: AIRequest
    ) -> AIDecision:
        pass