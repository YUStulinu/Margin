"""The language model: llama.cpp's llama-server (OpenAI-style chat endpoint), local. `serve()` starts
it from third_party/llama with a GGUF model; `Llm` talks to it."""

from __future__ import annotations

import os
import subprocess
import sys
import time
from pathlib import Path

import httpx

ROOT = Path(__file__).resolve().parents[2]


class Llm:
    def __init__(self, url: str = "http://127.0.0.1:8090", timeout: float = 180.0):
        self.url = url.rstrip("/")
        self.client = httpx.Client(timeout=timeout)
        self.calls = 0

    def chat(self, system: str, user: str, max_tokens: int = 300, temperature: float = 0.0) -> str:
        self.calls += 1
        r = self.client.post(
            f"{self.url}/v1/chat/completions",
            json={
                "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
                "max_tokens": max_tokens,
                "temperature": temperature,
                "cache_prompt": True,
            },
        )
        r.raise_for_status()
        return r.json()["choices"][0]["message"]["content"].strip()

    def ready(self) -> bool:
        try:
            return self.client.get(f"{self.url}/health", timeout=2).status_code == 200
        except httpx.HTTPError:
            return False


def serve(model: str | Path, port: int = 8090, context: int = 8192, gpu_layers: int = 99) -> subprocess.Popen:
    """Starts llama-server and waits until it answers."""
    exe = ROOT / "third_party" / "llama" / ("llama-server.exe" if sys.platform == "win32" else "llama-server")
    if not exe.exists():
        raise FileNotFoundError(f"{exe} not found: run scripts/fetch-deps.sh")
    p = subprocess.Popen(
        [str(exe), "-m", str(model), "--port", str(port), "-c", str(context), "-ngl", str(gpu_layers), "--parallel", "1", "--log-disable"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    llm = Llm(f"http://127.0.0.1:{port}")
    for _ in range(240):
        if p.poll() is not None:
            raise RuntimeError(f"llama-server exited ({p.returncode})")
        if llm.ready():
            return p
        time.sleep(0.5)
    p.kill()
    raise TimeoutError("llama-server did not start")


def default_model() -> Path:
    return Path(os.environ.get("MARGIN_LLM_MODEL", ROOT / "models" / "Qwen2.5-7B-Instruct-Q4_K_M.gguf"))
