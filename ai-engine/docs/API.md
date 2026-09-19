# API Documents
This is the file explaining how you can interact with out AI engine through http requests.

## Endpoints

### 1. Message Analyze
- Endpoint :  `POST /analyze-message`
- Request Format
    ```
    {
        "current_task": "Implement Logitech Actions SDK",
        "source": "slack",
        "sender": "teammate",
        "content": "Demo 改成下午兩點，請大家一點前更新投影片"
    }
    ```
- Response Format
    ```
    {
        "priority": "high",
        "relevant": true,
        "should_interrupt": true,
        "summary": "Demo 改到下午兩點，需一點前更新投影片",
        "suggested_action": "notify"
    }
    ```
POST /summarize
POST /draft-reply
