#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/env.sh"
cd "$repo_root"
exec dotnet run --project src/Ahr.Chatbot.Api --no-build --no-launch-profile -- --demo
