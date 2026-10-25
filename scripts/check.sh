#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/env.sh"
cd "$repo_root"
dotnet test tests/Ahr.Chatbot.Tests --no-restore --configuration Debug --logger 'console;verbosity=normal' --maxcpucount:2
node --test test/*.test.mjs
