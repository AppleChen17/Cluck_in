# Setup for Local LLM

## Install Ollama

Run the following command in PowerShell:

```powershell
irm https://ollama.com/install.ps1 | iex
```

Restart the terminal after installation.

Check whether Ollama is installed successfully:

```powershell
ollama --version
```

---

## Configure the Model

The AI Engine reads the Ollama model name from `config.py`.

Example:

```python
OLLAMA_BASE_URL = "http://localhost:11434"
OLLAMA_MODEL = "gemma3:4b"
```

Before starting the AI Engine, make sure the configured model has been downloaded.

For example:

```powershell
ollama pull gemma3:4b
```

You can check all locally installed models with:

```powershell
ollama list
```

---

## Test the Model

You can optionally test the configured model in interactive mode:

```powershell
ollama run gemma3:4b
```

When the `>>>` prompt appears, enter a message to verify that the model works correctly.

To leave interactive mode, enter:

```text
/bye
```

The model name used here should match `OLLAMA_MODEL` in `config.py`.

---

## Check Ollama Server

Ollama exposes its local API at the address configured by `OLLAMA_BASE_URL`.

The default value is:

```text
http://localhost:11434
```

You can verify that the server is running with:

```powershell
curl http://localhost:11434
```

A successful response should indicate that Ollama is running.

---

## Start the AI Engine

The AI Engine launcher (`start.py`) automatically checks whether the Ollama server is running and starts it when necessary.

Under normal usage, run:

```powershell
python start.py
```

You do not need to run `ollama serve` manually before starting the AI Engine.

To stop the system, press:

```text
Ctrl + C
```

If Ollama was started by `start.py`, it will also be stopped when the AI Engine shuts down.