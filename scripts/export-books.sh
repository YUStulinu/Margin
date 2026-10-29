#!/usr/bin/env bash
# Exports every book in data/books to data/json (text with positions), for the service's tests and
# evaluations. The app does this itself when it sends a book to the service.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet build "$root/src/Margin.Cli" -c Release -v quiet -nologo > /dev/null
mkdir -p "$root/data/json"
for f in "$root"/data/books/*.epub; do
  dotnet "$root/src/Margin.Cli/bin/Release/net10.0/Margin.Cli.dll" export "$f" "$root/data/json/$(basename "$f" .epub).json"
done
