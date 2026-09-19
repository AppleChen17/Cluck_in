# 127.0.0.1, never "localhost". Ollama binds IPv4 only; on Windows "localhost"
# resolves to ::1 first, that connection fails, and the IPv4 retry lands about
# 2 seconds later -- on every single request. Measured in
# src/external/docs/llm-findings.md ("The localhost trap"): 4.24s vs 2.81s for
# gemma3:1b on an identical request.
OLLAMA_BASE_URL = "http://127.0.0.1:11434"
OLLAMA_MODEL = "gemma3:4b"
