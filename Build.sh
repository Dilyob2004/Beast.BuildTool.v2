#!/usr/bin/env bash
# Builds Beast.BuildTool into BuildTool/bin (Release) on Linux and macOS. Usage: ./Build.sh
# Needs the .NET 10 SDK (or newer): bash InstallDotnet.sh installs it.
set -euo pipefail
cd "$(dirname "$0")"
sdk_major() { "$1" --list-sdks 2>/dev/null | sed -E 's/^([0-9]+)\..*/\1/' | sort -n | tail -n 1 || true; }
dotnet=""
for candidate in "${DOTNET_ROOT:+$DOTNET_ROOT/dotnet}" "$(command -v dotnet 2>/dev/null || true)" "$HOME/.dotnet/dotnet"; do
    [[ -n "$candidate" && -x "$candidate" ]] || continue
    major="$(sdk_major "$candidate")"
    if [[ -n "$major" && "$major" -ge 10 ]]; then dotnet="$candidate"; break; fi
done
[[ -n "$dotnet" ]] || { echo ".NET 10 SDK not found. Install it with: bash InstallDotnet.sh"; exit 1; }
"$dotnet" build BuildTool/Beast.BuildTool.csproj -c Release -nologo
echo "Built: $(pwd)/BuildTool/bin/Beast.BuildTool"
# The program finds the runtime through DOTNET_ROOT when .NET is not in a standard location (~/.dotnet).
root="$(cd "$(dirname "$dotnet")" && pwd -P)"
case "$root" in /usr/local/share/dotnet|/usr/share/dotnet|/usr/lib/dotnet|/usr/lib64/dotnet|/opt/homebrew/*|/usr/local/Cellar/*) ;;
    *) [[ "${DOTNET_ROOT:-}" == "$root" ]] || echo "Run it with: export DOTNET_ROOT=\"$root\"   (InstallDotnet.sh adds this to your shell profile)" ;;
esac
