# Document for AI Engine

This document provides a quick overview of how to start and use the AI Engine.

## To Start the Engine

1. Set up the environment. Please refer to [Setup.md](./docs/Setup.md).

2. Configure the Ollama model and server settings in [config.py](./config.py).

   Example:

   ```python
   OLLAMA_BASE_URL = "http://localhost:11434"
   OLLAMA_MODEL = "gemma3:4b"
   ```

   The model configured in `OLLAMA_MODEL` will be used by the Ollama provider for inference.

3. Install the required Python packages.

   ```powershell
   pip install -r requirements.txt
   ```

4. Start the AI Engine.

   ```powershell
   python start.py
   ```

    `start.py` will automatically check whether the Ollama server is running and start it if necessary.

By default, the AI Engine will be available at:

```text
http://127.0.0.1:8000
```

You can check whether the engine is running by visiting:

```text
http://127.0.0.1:8000/health
```

For development and API testing, FastAPI also provides Swagger UI at:

```text
http://127.0.0.1:8000/docs
```

To stop the system, press:

```text
Ctrl + C
```

If Ollama was started by `start.py`, it will also be stopped when the AI Engine shuts down.

---

## Available AI Functions

The AI Engine currently provides:

- Message analysis through `POST /analyze-message`
- Task and application access analysis through `POST /analyze-task`
- Filtered message summarization through `POST /summarize-messages`

For complete request and response formats, refer to [API.md](./docs/API.md).

---

## Other Information

- API usage: [API.md](./docs/API.md)
- Local LLM setup: [Setup.md](./docs/Setup.md)
- Schema definitions: [schemas.py](./schemas.py)
- Model configuration: [config.py](./config.py)