#!/usr/bin/env bash
# Source this file: source scripts/env.sh
export DOTNET_ROOT="${DOTNET_ROOT:-/workspace/.dotnet}"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/workspace/.dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-/workspace/.nuget/packages}"
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/workspace/.dotnet-runtime}"
export XDG_DATA_HOME="${XDG_DATA_HOME:-/workspace/.local/share}"
export XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-/workspace/.config}"
mkdir -p "$XDG_RUNTIME_DIR" "$XDG_DATA_HOME" "$XDG_CONFIG_HOME"
chmod 700 "$XDG_RUNTIME_DIR"
export PATH="$DOTNET_ROOT:$PATH"
