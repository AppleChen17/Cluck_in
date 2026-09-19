# API Documents

This file explains how to interact with our AI Engine through HTTP requests.

## Base URL

```text
http://127.0.0.1:8000
```

---

## Endpoints

### 1. Health Check

- Endpoint: `GET /health`

#### Response Format

```json
{
  "status": "ok",
  "provider": "OllamaProvider"
}
```

This endpoint is used to check whether the AI Engine is running correctly.

---

### 2. Message Analyze

- Endpoint: `POST /analyze-message`

The AI Engine analyzes an incoming message based on both the message content and the user's current work session.

#### Request Format

```json
{
  "message": {
    "id": "slack:123",
    "source": "slack",
    "sender": "teammate",
    "title": null,
    "content": "Demo 改成下午兩點，請大家一點前更新投影片",
    "timestamp": "2026-09-19T13:30:00+08:00",
    "unread": true
  },
  "context": {
    "mode": "focus",
    "currentTask": "Implement Logitech Actions SDK",
    "focusStartedAt": "2026-09-19T13:00:00+08:00",
    "focusDurationSeconds": 1500
  }
}
```

#### Request Fields

##### `message`

- `id`: Unique identifier of the message.
- `source`: Message source. Currently supports:
  - `"gmail"`
  - `"slack"`
- `sender`: Message sender.
- `title`: Message title. Can be `null`.
- `content`: Message content.
- `timestamp`: Message timestamp in ISO 8601 format.
- `unread`: Whether the message is unread.
- `metadata`: Optional additional information.

##### `context`

- `mode`: Current application mode:
  - `"idle"`
  - `"focus"`
  - `"auto"`
- `currentTask`: Current task of the user. Can be `null`.
- `focusStartedAt`: Start time of the current focus session. Can be `null`.
- `focusDurationSeconds`: Duration of the focus session in seconds. Can be `null`.
- `allowedApps`: Optional list of allowed applications.
- `blockedApps`: Optional list of blocked applications.
- `metadata`: Optional additional context.

#### Response Format

```json
{
  "messageId": "slack:123",
  "decision": "urgent",
  "relevance": 0.95,
  "urgency": 0.9,
  "reason": "此訊息直接影響目前正在進行的 Demo 準備，且具有明確的時間限制。"
}
```

#### Response Fields

- `messageId`: ID of the analyzed message.
- `decision`: AI decision:
  - `"urgent"`: The message should immediately get the user's attention.
  - `"allow"`: The message may be shown normally but does not necessarily need to interrupt the user.
  - `"hold"`: The message can be delayed until the focus session ends.
- `relevance`: Relevance to the current task, from `0.0` to `1.0`.
  - If `currentTask` is `null`, `relevance` will be `0`.
- `urgency`: Time urgency of the message, from `0.0` to `1.0`.
- `reason`: Short explanation of the AI decision.
- `metadata`: Optional additional output information.

When `mode` is `"idle"`, `"allow"` is normally used unless the message requires immediate attention.

---

### 3. Task Analyze

- Endpoint: `POST /analyze-task`

The AI Engine determines whether an application or webpage is relevant to the user's current task.

Whitelist and blacklist checks should be performed before sending the request to the AI Engine. This endpoint is intended for targets that require semantic analysis.

#### Request Format

```json
{
  "target": {
    "type": "web",
    "name": "FastAPI Documentation",
    "title": "Request Body - FastAPI",
    "url": "https://fastapi.tiangolo.com/tutorial/body/",
    "identifier": null
  },
  "context": {
    "mode": "focus",
    "currentTask": "Implement AI Engine API",
    "automationMode": "suggestion",
    "focusStartedAt": "2026-09-19T16:00:00+08:00",
    "focusDurationSeconds": 1500
  }
}
```

#### Request Fields

##### `target`

- `type`: Type of the requested target:
  - `"app"`
  - `"web"`
- `name`: Application or website name.
- `title`: Current page or window title. Can be `null`.
- `url`: Target URL. Mainly used for webpages. Can be `null`.
- `identifier`: Application identifier, such as an executable name. Can be `null`.

##### `context`

- `mode`: Current application mode:
  - `"idle"`
  - `"focus"`
  - `"auto"`
- `currentTask`: Current task of the user. Can be `null`.
- `focusStartedAt`: Start time of the current focus session. Can be `null`.
- `focusDurationSeconds`: Duration of the focus session in seconds. Can be `null`.
- `automationMode`: Controls automated reply behavior:
  - `"on"`: Generate reply content that may be executed automatically by the main program.
  - `"suggestion"`: Generate reply content for user approval.
  - `"off"`: Do not generate reply content.
- `allowedApps`: Optional list of allowed applications.
- `blockedApps`: Optional list of blocked applications.
- `metadata`: Optional additional context.

#### Response Format

```json
{
  "decision": "allow",
  "relevance": 0.9,
  "reason": "目標是 FastAPI 文件，與目前正在進行的 AI Engine API 實作高度相關。",
  "requiresReply": true,
  "replyDraft": "可以，我今晚八點會到，謝謝通知！"
}
```

#### Response Fields

- `decision`: AI decision:
  - `"allow"`: The target is clearly related to the current task.
  - `"warn"`: The target may be useful, but its purpose is unclear.
  - `"block"`: The target is clearly unrelated to the current task during focus mode.
- `relevance`: Relevance between the target and the current task, from `0.0` to `1.0`.
- `requiresReply`: Whether the incoming message requires a reply.
- `replyDraft`: Generated reply content. This is `null` when no reply is required or when `automationMode` is `"off"`.
- `reason`: Short explanation of the AI decision.

When `mode` is `"idle"`, `"allow"` is normally returned.

If `currentTask` is `null`, the target should not be blocked only because task relevance cannot be determined.

---