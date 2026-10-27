#!/usr/bin/env bash
# Installs the .NET 10 SDK needed to build Beast BuildTool (Linux, macOS), without sudo.
#   bash InstallDotnet.sh                 install into ~/.dotnet (if no .NET 10+ SDK is found) and add it to PATH
#   bash InstallDotnet.sh --dir <path>    install somewhere else
#   bash InstallDotnet.sh --force         install even if a .NET 10+ SDK is already present
#   bash InstallDotnet.sh --no-profile    do not edit ~/.zshrc / ~/.bashrc / ~/.profile
# Uses Microsoft's official dotnet-install.sh (https://dot.net/v1/dotnet-install.sh), channel 10.0.
set -euo pipefail
channel=10.0
dir="${DOTNET_INSTALL_DIR:-$HOME/.dotnet}"
force=0; profile=1
while [[ $# -gt 0 ]]; do
    case "$1" in
        --dir) dir="$2"; shift 2 ;;
        --force) force=1; shift ;;
        --no-profile) profile=0; shift ;;
        -h|--help) sed -n '2,7p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

# Major version of the newest SDK a dotnet executable has (0 when none).
sdk_major() { "$1" --list-sdks 2>/dev/null | sed -E 's/^([0-9]+)\..*/\1/' | sort -n | tail -n 1 || true; }

if [[ $force == 0 ]]; then
    for candidate in "$(command -v dotnet 2>/dev/null || true)" "$dir/dotnet"; do
        [[ -n "$candidate" && -x "$candidate" ]] || continue
        major="$(sdk_major "$candidate")"
        if [[ -n "$major" && "$major" -ge 10 ]]; then
            echo ".NET SDK $("$candidate" --version) is already installed ($candidate). Nothing to do."
            exit 0
        fi
    done
fi

case "$(uname -s)" in
    Darwin) ;;
    Linux)
        # The SDK needs ICU (or invariant globalization); say so instead of failing later.
        if ! ldconfig -p 2>/dev/null | grep -q libicu && ! ls /usr/lib*/libicu* /usr/lib/*/libicu* >/dev/null 2>&1; then
            echo "Note: libicu was not found. Install it (Ubuntu/Debian: sudo apt install libicu-dev; Fedora: sudo dnf install libicu)"
            echo "      or set DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1."
        fi ;;
    *) echo "Use InstallDotnet.ps1 on Windows."; exit 1 ;;
esac

script="$(mktemp -t dotnet-install.XXXXXX)"
trap 'rm -f "$script"' EXIT
echo "Downloading dotnet-install.sh..."
if command -v curl >/dev/null; then curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$script"
elif command -v wget >/dev/null; then wget -q https://dot.net/v1/dotnet-install.sh -O "$script"
else echo "curl or wget is required."; exit 1; fi

echo "Installing the .NET $channel SDK into $dir..."
bash "$script" --channel "$channel" --install-dir "$dir" --no-path
"$dir/dotnet" --version >/dev/null
echo "Installed .NET SDK $("$dir/dotnet" --version) into $dir"

if [[ $profile == 1 ]]; then
    case "${SHELL:-}" in
        */zsh) rc="$HOME/.zshrc" ;;
        */bash) [[ "$(uname -s)" == Darwin ]] && rc="$HOME/.bash_profile" || rc="$HOME/.bashrc" ;;
        *) rc="$HOME/.profile" ;;
    esac
    line="export DOTNET_ROOT=\"$dir\"; export PATH=\"\$DOTNET_ROOT:\$PATH\""
    if ! grep -qsF "$line" "$rc"; then
        printf '\n# .NET SDK (added by Beast InstallDotnet.sh)\n%s\n' "$line" >>"$rc"
        echo "Added .NET to PATH in $rc."
    fi
    echo "Open a new terminal (or run: source $rc), then: dotnet --version"
else
    echo "PATH was not changed. Use: export DOTNET_ROOT=\"$dir\"; export PATH=\"\$DOTNET_ROOT:\$PATH\""
fi
