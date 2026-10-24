#!/usr/bin/env bash
# Builds Beast.BuildTool into BuildTool/bin (Release) on Linux and macOS. Usage: ./Build.sh
set -euo pipefail
cd "$(dirname "$0")"
command -v dotnet >/dev/null || { echo ".NET SDK not found. Install .NET 8 SDK or newer."; exit 1; }
dotnet build BuildTool/Beast.BuildTool.csproj -c Release -nologo
echo "Built: $(pwd)/BuildTool/bin/Beast.BuildTool"
