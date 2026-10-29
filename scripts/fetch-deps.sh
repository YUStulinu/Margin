#!/usr/bin/env bash
# Downloads what Margin's companion uses but does not keep in git, checks each file's SHA-256 and
# unpacks it: the llama.cpp server (b11429; the CUDA 13 build on Windows, --cpu for the CPU one; the
# CPU build on Linux). The books in data/books are in git (public domain, from Project Gutenberg).
#   scripts/fetch-deps.sh [--cpu] [--llm] [--llm3]
#     --llm    also Qwen2.5-7B-Instruct Q4_K_M (the companion's model) into models/
#     --llm3   also Qwen2.5-3B-Instruct Q4_K_M (smaller, for weaker GPUs) into models/
# The embedding model (BAAI/bge-small-en-v1.5) is downloaded by the service on first use.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dl="$root/third_party/downloads"
mkdir -p "$dl" "$root/models"
cpu=0 llm=0 llm3=0
for a in "$@"; do
  case "$a" in
    --cpu) cpu=1 ;;
    --llm) llm=1 ;;
    --llm3) llm3=1 ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done

fetch() { # url sha256 file
  local url="$1" sha="$2" file="$dl/$3"
  if [ ! -f "$file" ] || ! echo "$sha  $file" | sha256sum -c - >/dev/null 2>&1; then
    echo "downloading $url"
    curl -fsSL --retry 3 -o "$file.part" "$url" || { echo "download failed: $url" >&2; exit 1; }
    echo "$sha  $file.part" | sha256sum -c - >/dev/null || { echo "checksum mismatch: $url" >&2; rm -f "$file.part"; exit 1; }
    mv "$file.part" "$file"
  fi
}

llama="https://github.com/ggml-org/llama.cpp/releases/download/b11429"
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*)
    if [ $cpu = 1 ]; then
      fetch "$llama/llama-b11429-bin-win-cpu-x64.zip" 1283323272b04cd07905816a597a0da810918102de958f4ff6f7bbaa70ed2efe llama-b11429-bin-win-cpu-x64.zip
      zips="llama-b11429-bin-win-cpu-x64.zip"
    else
      fetch "$llama/llama-b11429-bin-win-cuda-13.4-x64.zip" 76ddc6eff2389570789ed608881efc6977751a722015d8e8c94f302224ff1a3a llama-b11429-bin-win-cuda-13.4-x64.zip
      fetch "$llama/cudart-llama-bin-win-cuda-13.4-x64.zip" 738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668 cudart-llama-bin-win-cuda-13.4-x64.zip
      zips="llama-b11429-bin-win-cuda-13.4-x64.zip cudart-llama-bin-win-cuda-13.4-x64.zip"
    fi
    if [ ! -f "$root/third_party/llama/llama-server.exe" ]; then
      mkdir -p "$root/third_party/llama"
      for z in $zips; do unzip -qo "$dl/$z" -d "$root/third_party/llama"; done
    fi ;;
  Linux)
    fetch "$llama/llama-b11429-bin-ubuntu-x64.tar.gz" f6d25dde8f51133143d1453da4fd5f73b145127177612a283bf7995957af3392 llama-b11429-bin-ubuntu-x64.tar.gz
    if [ ! -x "$root/third_party/llama/llama-server" ]; then
      mkdir -p "$root/third_party/llama"
      tar -xzf "$dl/llama-b11429-bin-ubuntu-x64.tar.gz" -C "$root/third_party/llama" --strip-components=1
    fi ;;
esac

if [ $llm = 1 ]; then
  fetch https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf \
    65b8fcd92af6b4fefa935c625d1ac27ea29dcb6ee14589c55a8f115ceaaa1423 Qwen2.5-7B-Instruct-Q4_K_M.gguf
  mv -f "$dl/Qwen2.5-7B-Instruct-Q4_K_M.gguf" "$root/models/"
fi
if [ $llm3 = 1 ]; then
  fetch https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf \
    9c9f56a391a3abbd5b89d0245bf6106081bcc3173119d4229235dd9d23253f94 Qwen2.5-3B-Instruct-Q4_K_M.gguf
  mv -f "$dl/Qwen2.5-3B-Instruct-Q4_K_M.gguf" "$root/models/"
fi
echo "dependencies are in $root/third_party"
