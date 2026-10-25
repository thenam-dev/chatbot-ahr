#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/env.sh"
cd "$repo_root"

sdk_version=8.0.426
sdk_sha512=8141d01bdc53f604aed3bb661b604707a64209f8a947cd81bf95b4f518951593d77864069191f54ea5e84362a409da6b0f69074bf893b283d7e0cf93e2bf20a8
if [[ ! -x "$DOTNET_ROOT/dotnet" ]] || ! "$DOTNET_ROOT/dotnet" --list-sdks | rg -q "^${sdk_version//./\.} "; then
    if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
        echo "Install .NET SDK $sdk_version for your platform from dotnet.microsoft.com; this bootstrap targets Linux x64." >&2
        exit 1
    fi
    sdk_archive="$(mktemp /tmp/ahr-dotnet-sdk.XXXXXX.tar.gz)"
    trap 'rm -f -- "$sdk_archive"' EXIT
    curl --fail --location --show-error --silent --retry 2 \
        "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdk_version/dotnet-sdk-$sdk_version-linux-x64.tar.gz" \
        --output "$sdk_archive"
    printf '%s  %s\n' "$sdk_sha512" "$sdk_archive" | sha512sum --check --status
    mkdir -p "$DOTNET_ROOT"
    tar -xzf "$sdk_archive" -C "$DOTNET_ROOT"
fi

dotnet restore Ahr.Chatbot.sln --locked-mode --disable-parallel
dotnet build Ahr.Chatbot.sln --no-restore --configuration Debug --maxcpucount:2
echo "Setup complete. Start the local demo with: bash scripts/run-demo.sh"
