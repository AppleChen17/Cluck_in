import time
import requests
import subprocess
import json

from config import OLLAMA_BASE_URL, OLLAMA_MODEL
from schemas import (
    AIRequest,
    AIDecision,
    TaskAnalyzeRequest,
    TaskDecision,
    MessageSummaryResponse,
    MessageSummaryRequest
)
from services.llm_provider import LLMProvider


class OllamaProvider(LLMProvider):

    MESSAGE_SYSTEM_PROMPT = """
    你是工作訊息分類器，只輸出合法 JSON。

    判斷順序：
    1. requiresReply
    2. decision
    3. relevance
    4. urgency
    5. replyDraft

    requiresReply:
    - requiresReply 只表示「寄件者是否明確期待收件者回應」，不是「禮貌上是否可以回覆」
    - 有直接問題、確認要求、選擇要求、資訊要求、明確要求回覆 → true
    - 純通知、公告、提醒、狀態更新、時間異動 → false
    - 「謝謝」、「敬請見諒」、「請知悉」、「供參考」等禮貌用語不代表需要回覆
    - 即使可以禮貌回覆，只要寄件者沒有要求或期待回應，requiresReply 仍為 false
    - 即使訊息前半段是通知，只要後半段有直接問題或要求確認，requiresReply 才為 true

    decision:
    - urgent: 必須立即注意
    - allow: 正常顯示
    - hold: 可延後處理

    relevance:
    - 與 currentTask 的相關程度 0~1
    - currentTask=null → 0

    urgency:
    - 時效性 0~1

    replyDraft:
    - requiresReply=false → null
    - automationMode=off → null
    - requiresReply=true 且 automationMode=on/suggestion → 產生簡短回覆
    - 不得捏造未知資訊

    所有 reason 與 replyDraft 使用臺灣繁體中文。
    messageId 必須與輸入完全一致。

    輸出：
    {
    "messageId": "string",
    "decision": "urgent|allow|hold",
    "relevance": 0.0,
    "urgency": 0.0,
    "requiresReply": true,
    "replyDraft": null,
    "reason": "string"
    }
    """

    TASK_SYSTEM_PROMPT = """
    你是專注模式下的應用程式與網頁存取分類器。

    你的任務是判斷使用者準備開啟的目標，
    是否與目前工作相關。

    decision:
    - allow: 與目前工作明確相關，可以開啟
    - warn: 可能與目前工作相關，但用途不夠明確
    - block: 與目前工作明顯無關，不建議在專注模式開啟

    規則:
    - relevance 表示 target 與 currentTask 的相關程度，範圍 0 到 1
    - 如果 mode 是 idle，decision 應為 allow
    - 如果 currentTask 是 null，不應因缺乏相關性而 block
    - 判斷時可參考 app/web 名稱、title、URL
    - reason 使用臺灣繁體中文
    - 只能輸出合法 JSON
    - 不可以輸出 Markdown 或額外說明

    輸出格式:
    {
    "decision": "allow | warn | block",
    "relevance": 0.0,
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
                "options": {
                    "temperature": 0
                },
            },
            timeout=60,
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

        print("\n[Ollama] process status:")
        subprocess.run(["ollama", "ps"])

        return data["message"]["content"]
    
    def analyze_message(
        self,
        request: AIRequest,
    ) -> AIDecision:

        message = request.message
        context = request.context

        current_task = context.currentTask or "null"
        title = message.title or "null"

        user_prompt = f"""
        mode: {context.mode}
        currentTask: {current_task}
        automationMode: {context.automationMode}

        messageId: {message.id}
        source: {message.source}
        sender: {message.sender}
        title: {title}
        content: {message.content}
        """


        result = self._chat([
            {
                "role": "system",
                "content": self.MESSAGE_SYSTEM_PROMPT,
            },

            # Positive example
            {
                "role": "user",
                "content": """
        mode: focus
        currentTask: Prepare Demo
        automationMode: suggestion

        messageId: example-reply
        source: slack
        sender: teammate
        title: null
        content: Demo 改到晚上八點，你可以確認你會到嗎？
        """
            },
            {
                "role": "assistant",
                "content": """
        {
        "messageId": "example-reply",
        "decision": "allow",
        "relevance": 0.9,
        "urgency": 0.7,
        "requiresReply": true,
        "replyDraft": "可以，我會到，謝謝通知！",
        "reason": "對方要求確認是否出席，因此需要回覆。"
        }
        """
            },

            # Negative example
            {
                "role": "user",
                "content": """
        mode: idle
        currentTask: null
        automationMode: on

        messageId: example-notice
        source: gmail
        sender: organizer@example.com
        title: 設施維修通知
        content: 淋浴間今天臨時維修，暫停使用，敬請見諒。
        """
            },
            {
                "role": "assistant",
                "content": """
        {
        "messageId": "example-notice",
        "decision": "allow",
        "relevance": 0.0,
        "urgency": 0.3,
        "requiresReply": false,
        "replyDraft": null,
        "reason": "此訊息為單向維修通知，沒有要求收件者回覆。"
        }
        """
            },

            # Real request
            {
                "role": "user",
                "content": user_prompt,
            },
        ])

        decision = AIDecision.model_validate_json(result)

        # Enforce automation behavior deterministically.
        if not decision.requiresReply:
            decision.replyDraft = None

        elif context.automationMode == "off":
            decision.replyDraft = None


        return decision

    def analyze_task(
        self,
        request: TaskAnalyzeRequest,
    ) -> TaskDecision:

        target = request.target
        context = request.context

        user_prompt = f"""
        mode: {context.mode}
        currentTask: {context.currentTask or "null"}

        targetType: {target.type}
        name: {target.name}
        title: {target.title or "null"}
        url: {target.url or "null"}
        identifier: {target.identifier or "null"}
        """

        result = self._chat([
            {
                "role": "system",
                "content": self.TASK_SYSTEM_PROMPT,
            },
            {
                "role": "user",
                "content": user_prompt,
            },
        ])

        return TaskDecision.model_validate_json(result)

    def summarize_messages(
        self,
        request: MessageSummaryRequest
    ) -> MessageSummaryResponse:

        if not request.messages:
            return MessageSummaryResponse(
                messageCount=0,
                summary="目前沒有被暫緩的訊息。"
            )

        message_text = "\n\n".join(
            f"""
    Message {index + 1}
    source: {message.source}
    sender: {message.sender}
    title: {message.title}
    timestamp: {message.timestamp}
    content: {message.content}
    """.strip()
            for index, message in enumerate(request.messages)
        )

        prompt = f"""
    你是訊息摘要助手。

    以下是使用者進入 Focus Mode 後被暫緩的訊息。

    請整理成一段簡短摘要，讓使用者可以快速知道專注期間發生什麼事。

    規則：
    - 保留重要人物、時間、地點、任務與異動
    - 保留原訊息中的時間與完成狀態，不得將未完成或未來事件描述為已完成。
    - 不要加入原訊息沒有的資訊
    - 合併重複內容
    - 不需要逐封列出
    - 使用臺灣繁體中文
    - 簡潔易讀
    - 只輸出 JSON

    訊息：
    {message_text}

    輸出：
    {{
    "summary": "string"
    }}
    """

        result = self._chat([
            {
                "role": "user",
                "content": prompt
            }
        ])

        data = json.loads(result)

        return MessageSummaryResponse(
            messageCount=len(request.messages),
            summary=data["summary"]
        )