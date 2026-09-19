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

## Download and Test the Model

Run the configured model in interactive mode:

```powershell
ollama run gemma3:4b
```

When the `>>>` prompt appears, try entering a message to verify that the model works correctly.

To leave interactive mode, enter:

```text
/bye
```

---

## Check Ollama Server

Ollama normally exposes its local API at:

```text
http://localhost:11434
```

You can verify that the server is running with:

```powershell
curl http://localhost:11434
```

A successful response should indicate that Ollama is running.

---

## Notes

The AI Engine launcher (`start.py`) automatically checks whether the Ollama server is running and starts it when necessary.

Therefore, under normal usage, you only need to run:

```powershell
python start.py
```