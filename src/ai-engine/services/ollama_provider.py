import time
import requests

from config import OLLAMA_BASE_URL, OLLAMA_MODEL
from schemas import AIRequest, AIDecision
from services.llm_provider import LLMProvider


class OllamaProvider(LLMProvider):

    SYSTEM_PROMPT = """
你是工作訊息分類器，只能輸出合法 JSON。

decision:
- urgent: 延遲處理可能造成明顯問題，或訊息要求立即處理
- allow: 可以正常顯示，但不需要立即中斷使用者
- hold: 可以延後到專注結束後再處理

規則:
- relevance 表示訊息與 currentTask 的相關程度，範圍 0 到 1
- 如果 currentTask 為 null，relevance 必須為 0
- urgency 表示訊息的時間急迫程度，範圍 0 到 1
- 如果 mode 是 idle，原則上 decision 使用 allow
- idle 模式下，只有訊息明確要求立即處理時才使用 urgent
- hold 主要用於 focus 模式下，且訊息與目前工作無關或不急
- reason 必須簡短，並使用臺灣繁體中文
- messageId 必須和輸入完全一致
- 不要輸出 Markdown
- 不要輸出任何 JSON 以外的文字

輸出格式:
{
  "messageId": "string",
  "decision": "urgent | allow | hold",
  "relevance": 0.0,
  "urgency": 0.0,
  "reason": "string"
}
"""

    def _chat(self, messages: list[dict]) -> str:
        start = time.perf_counter()

        response = requests.post(
            f"{OLLAMA_BASE_URL}/api/chat",
            json={
                "model": OLLAMA_MODEL,
                "messages": messages,
                "stream": False,
                "format": "json",
                "keep_alive": "10m",
            },
            timeout=30,
        )

        response.raise_for_status()

        elapsed = time.perf_counter() - start
        data = response.json()

        print(f"[Ollama] total response time: {elapsed:.2f}s")

        if "load_duration" in data:
            print(
                f"[Ollama] load duration: "
                f"{data['load_duration'] / 1_000_000_000:.2f}s"
            )

        if "prompt_eval_duration" in data:
            print(
                f"[Ollama] prompt eval duration: "
                f"{data['prompt_eval_duration'] / 1_000_000_000:.2f}s"
            )

        if "eval_duration" in data:
            print(
                f"[Ollama] generation duration: "
                f"{data['eval_duration'] / 1_000_000_000:.2f}s"
            )

        return data["message"]["content"]

    def analyze_message(
        self,
        request: AIRequest
    ) -> AIDecision:

        message = request.message
        context = request.context

        current_task = context.currentTask or "null"
        title = message.title or "null"

        user_prompt = f"""
mode: {context.mode}
currentTask: {current_task}

messageId: {message.id}
source: {message.source}
sender: {message.sender}
title: {title}
content: {message.content}
"""

        result = self._chat([
            {
                "role": "system",
                "content": self.SYSTEM_PROMPT,
            },
            {
                "role": "user",
                "content": user_prompt,
            },
        ])

        return AIDecision.model_validate_json(result)