import subprocess
import time
import requests
import sys
from config import OLLAMA_BASE_URL as OLLAMA_URL



def is_ollama_running():
    try:
        return requests.get(OLLAMA_URL, timeout=1).status_code == 200
    except requests.RequestException:
        return False


def start_ollama():
    if is_ollama_running():
        print("[System] Ollama already running")
        return None

    print("[System] Starting Ollama...")
    process = subprocess.Popen(
        ["ollama", "serve"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    for _ in range(20):
        time.sleep(0.5)
        if is_ollama_running():
            print("[System] Ollama ready")
            return process

    raise RuntimeError("Failed to start Ollama")


def main():
    ollama_process = None

    try:
        ollama_process = start_ollama()

        print("[System] Starting AI Engine...")

        subprocess.run([
            sys.executable,
            "-m",
            "uvicorn",
            "app:app",
            "--reload",
        ])

    except KeyboardInterrupt:
        print("\n[System] Shutting down...")

    finally:
        if ollama_process:
            ollama_process.terminate()
            ollama_process.wait()
            print("[System] Ollama stopped")


if __name__ == "__main__":
    main()