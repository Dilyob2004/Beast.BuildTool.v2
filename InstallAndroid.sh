#!/usr/bin/env bash
# Installs the Android toolchain Beast needs (Linux, macOS), without sudo:
#   JDK 17 (Eclipse Temurin), Android SDK command-line tools, platform-tools, platforms;android-35,
#   build-tools;35.0.0 and the NDK; optionally the emulator, an x86_64/arm64 system image and AVD beast_api34.
#
#   bash InstallAndroid.sh                    SDK in the Android Studio location (~/Android/Sdk, ~/Library/Android/sdk)
#   bash InstallAndroid.sh --dir <path>       SDK somewhere else
#   bash InstallAndroid.sh --jdk-dir <path>   JDK 17 somewhere else (default ~/.local/share/beast/jdk-17,
#                                             macOS ~/Library/Java/JavaVirtualMachines/beast-temurin-17)
#   bash InstallAndroid.sh --ndk <version>    NDK version (default: the one Beast's CI uses)
#   bash InstallAndroid.sh --cmdline-tools <v> command-line tools version (default 19.0: its sdkmanager works
#                                             on its own; 23.0+ hands over to "Android CLI", which collects usage metrics)
#   bash InstallAndroid.sh --emulator         also the emulator, a system image and AVD beast_api34
#   bash InstallAndroid.sh --force            download again what is already installed (including the JDK)
#   bash InstallAndroid.sh --no-profile       do not edit ~/.zshrc / ~/.bashrc / ~/.profile
# Licenses are accepted automatically (sdkmanager --licenses, where still needed). Sources: dl.google.com (repository
# catalogue, SHA-1 checked) and api.adoptium.net (Temurin, SHA-256 checked).
set -euo pipefail
# In GitHub Actions a failure is also an annotation: the failing command and its line.
[[ -n "${GITHUB_ACTIONS:-}" ]] && trap 'echo "::error title=InstallAndroid.sh::line $LINENO: $BASH_COMMAND failed"' ERR
NDK_DEFAULT=27.3.13750724
# sdkmanager of cmdline-tools 23.0+ is deprecated in favour of "Android CLI": it downloads that tool and
# enables Google usage metrics. 19.0 is the pinned default (archives and SHA-1 from Google's catalogue).
CMDLINE_TOOLS_DEFAULT=19.0
PLATFORM=android-35
BUILD_TOOLS=35.0.0
AVD=beast_api34
REPOSITORY=https://dl.google.com/android/repository

case "$(uname -s)" in
    Darwin) os=mac; sdk_default="$HOME/Library/Android/sdk"; jdk_default="$HOME/Library/Java/JavaVirtualMachines/beast-temurin-17" ;;
    Linux) os=linux; sdk_default="$HOME/Android/Sdk"; jdk_default="$HOME/.local/share/beast/jdk-17" ;;
    *) echo "Use InstallAndroid.ps1 on Windows."; exit 1 ;;
esac
case "$(uname -m)" in arm64|aarch64) arch=aarch64 ;; *) arch=x64 ;; esac

dir="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$sdk_default}}"; jdk_dir="$jdk_default"; ndk="$NDK_DEFAULT"; tools="$CMDLINE_TOOLS_DEFAULT"
emulator=0; force=0; profile=1
while [[ $# -gt 0 ]]; do
    case "$1" in
        --dir) dir="$2"; shift 2 ;;
        --jdk-dir) jdk_dir="$2"; shift 2 ;;
        --ndk) ndk="$2"; shift 2 ;;
        --cmdline-tools) tools="$2"; shift 2 ;;
        --emulator) emulator=1; shift ;;
        --force) force=1; shift ;;
        --no-profile) profile=0; shift ;;
        -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done
mkdir -p "$dir"; dir="$(cd "$dir" && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/beast-android.XXXXXX")"
trap 'rm -rf "$work"' EXIT

download() {
    echo "Downloading $1"
    # --retry-all-errors: also DNS and connection failures, 5 seconds apart.
    curl -fL --retry 3 --retry-delay 5 --retry-all-errors --progress-bar -o "$2" "$1" ||
        { echo "Download failed: $1. The cause is most likely the network: check the connection to $(echo "$1" | sed -E 's#^[a-z]+://([^/]+).*#\1#') (DNS, proxy, firewall)."; return 1; }
}
sha() { if command -v sha"$1"sum >/dev/null; then sha"$1"sum "$2" | cut -d' ' -f1; else shasum -a "$1" "$2" | cut -d' ' -f1; fi; }
java_major() { "$1/bin/java" -version 2>&1 | sed -nE 's/.*version "(1\.)?([0-9]+).*/\2/p' | head -n 1; }

# --- JDK 17+ -----------------------------------------------------------------------------------
jdk=""
if [[ $force == 0 ]]; then
    for candidate in "${JAVA_HOME:-}" "$jdk_dir/Contents/Home" "$jdk_dir" \
        "/Applications/Android Studio.app/Contents/jbr/Contents/Home" "$HOME/Applications/Android Studio.app/Contents/jbr/Contents/Home" \
        /opt/android-studio/jbr "$HOME/android-studio/jbr" /usr/local/android-studio/jbr; do
        [[ -n "$candidate" && -x "$candidate/bin/java" ]] || continue
        major="$(java_major "$candidate")"
        if [[ -n "$major" && "$major" -ge 17 ]]; then jdk="$candidate"; echo "JDK $major found: $jdk"; break; fi
    done
fi
if [[ -z "$jdk" ]]; then
    echo "Looking up Eclipse Temurin JDK 17 ($os $arch)..."
    # The release's .tar.gz and its published .sha256.txt (GitHub release assets).
    curl -fsSL --retry 3 -o "$work/jdk.json" "https://api.adoptium.net/v3/assets/latest/17/hotspot?os=$os&architecture=$arch&image_type=jdk&vendor=eclipse"
    link="$(grep -oE '"link"[[:space:]]*:[[:space:]]*"[^"]+\.tar\.gz"' "$work/jdk.json" | head -n 1 | sed -E 's/.*"(https[^"]+)"$/\1/')" || true
    [[ -n "$link" ]] || { echo "No Temurin JDK 17 .tar.gz for $os $arch in the Adoptium API answer"; exit 1; }
    download "$link" "$work/jdk.tar.gz"
    expected="$(curl -fsSL --retry 3 "$link.sha256.txt" | cut -d' ' -f1)"
    [[ "$(sha 256 "$work/jdk.tar.gz")" == "$expected" ]] || { echo "JDK checksum mismatch ($link)"; exit 1; }
    mkdir -p "$work/jdk" && tar -xzf "$work/jdk.tar.gz" -C "$work/jdk"
    rm -rf "$jdk_dir"; mkdir -p "$(dirname "$jdk_dir")"
    mv "$work/jdk/"*/ "$jdk_dir"
    jdk="$jdk_dir"; [[ -x "$jdk/Contents/Home/bin/java" ]] && jdk="$jdk/Contents/Home"
    echo "JDK $(java_major "$jdk") installed: $jdk"
fi
export JAVA_HOME="$jdk"

# --- Command-line tools (sdkmanager) ------------------------------------------------------------
# Installed as cmdline-tools/<version> (not latest/), so an Android Studio cmdline-tools/latest of
# another version is neither used nor replaced.
tools_dir="$dir/cmdline-tools/$tools"
sdkmanager="$tools_dir/bin/sdkmanager"
tools_major="${tools%%.*}"
if [[ "$tools_major" =~ ^[0-9]+$ && "$tools_major" -ge 23 ]]; then
    echo "Warning: cmdline-tools $tools: its sdkmanager hands over to Android CLI, which collects usage metrics."
fi
if [[ $force == 1 || ! -x "$sdkmanager" ]]; then
    # cmdline-tools;<version> for this OS (and architecture, when the catalogue has one) from Google's catalogue.
    download "$REPOSITORY/repository2-3.xml" "$work/repository.xml"
    [[ $os == mac ]] && host=macosx || host=linux
    [[ $arch == aarch64 ]] && archs="aarch64 arm64" || archs="x64 x86_64"
    read -r file checksum < <(awk -v host="$host" -v archs=" $archs " -v package="cmdline-tools;$tools" '
        index($0, "<remotePackage path=\"" package "\"") { inside = 1; next }
        inside && /<\/remotePackage>/ { inside = 0; next }
        inside && /<archive>/ { u = ""; c = ""; o = ""; a = "" }
        inside { if (match($0, /<url>[^<]+<\/url>/)) u = substr($0, RSTART + 5, RLENGTH - 11)
                 if (match($0, /<checksum[^>]*>[^<]+<\/checksum>/)) { s = substr($0, RSTART, RLENGTH); sub(/<checksum[^>]*>/, "", s); sub(/<\/checksum>/, "", s); c = s }
                 if (match($0, /<host-os>[^<]+<\/host-os>/)) o = substr($0, RSTART + 9, RLENGTH - 19)
                 if (match($0, /<host-arch>[^<]+<\/host-arch>/)) a = substr($0, RSTART + 11, RLENGTH - 23) }
        inside && /<\/archive>/ && o == host {
            if (a == "" && best == "") best = u " " c
            if (a != "" && index(archs, " " a " ")) exact = u " " c
            # mac_arm64/mac_x86_64 archives without host-arch: tell them apart by name.
            if (a == "" && ((index(u, "arm64") && index(archs, "arm64")) || (index(u, "x86_64") && index(archs, "x86_64")))) exact = u " " c }
        END { print (exact != "" ? exact : best) }' "$work/repository.xml")
    [[ -n "${file:-}" ]] || { echo "cmdline-tools $tools for $host not found in $REPOSITORY/repository2-3.xml"; exit 1; }
    download "$REPOSITORY/$file" "$work/cmdline-tools.zip"
    [[ "$(sha 1 "$work/cmdline-tools.zip")" == "$checksum" ]] || { echo "cmdline-tools checksum mismatch ($file)"; exit 1; }
    mkdir -p "$work/cmdline-tools" && unzip -q "$work/cmdline-tools.zip" -d "$work/cmdline-tools"
    rm -rf "$tools_dir"; mkdir -p "$dir/cmdline-tools"
    mv "$work/cmdline-tools/cmdline-tools" "$tools_dir"
    echo "Command-line tools $tools installed: $tools_dir ($file)"
else
    echo "Already installed: cmdline-tools $tools"
fi

# --- Packages ------------------------------------------------------------------------------------
[[ "$os/$arch" == mac/aarch64 ]] && image_abi=arm64-v8a || image_abi=x86_64
image="system-images;android-34;google_apis;$image_abi"
packages=(); markers=()
# A package counts as installed when its marker file exists; sdkmanager runs only for the others.
need() { if [[ $force == 1 || ! -e "$dir/$2" ]]; then packages+=("$1"); markers+=("$2"); else echo "Already installed: $1"; fi; }
need platform-tools platform-tools/adb
need "platforms;$PLATFORM" "platforms/$PLATFORM/android.jar"
need "build-tools;$BUILD_TOOLS" "build-tools/$BUILD_TOOLS/aapt2"
need "ndk;$ndk" "ndk/$ndk/source.properties"
if [[ $emulator == 1 ]]; then
    need emulator emulator/emulator
    # The AVD's target (android-34): avdmanager does not list an AVD whose platform is missing.
    need "platforms;android-34" "platforms/android-34/android.jar"
    need "$image" "system-images/android-34/google_apis/$image_abi/system.img"
fi
# Licenses: sdkmanager up to 22.0 needs them accepted first; 23.0+ says --licenses is no longer needed.
if ! [[ "$tools_major" =~ ^[0-9]+$ && "$tools_major" -ge 23 ]]; then
    yes 2>/dev/null | "$sdkmanager" --sdk_root="$dir" --licenses >/dev/null || true
fi
# Network or DNS failures ("An error occurred while preparing SDK package ...: dl.google.com") leave
# packages out: the missing ones are installed again, up to 3 attempts, 5 then 10 seconds apart.
# BEAST_INSTALL_ANDROID_FAIL_ATTEMPTS=<n> (tests) sends the first n attempts through an unreachable proxy.
attempt=0
while [[ ${#packages[@]} -gt 0 ]]; do
    attempt=$((attempt + 1))
    if [[ $attempt -eq 1 ]]; then echo "Installing: ${packages[*]}"; else echo "Installing (attempt $attempt of 3): ${packages[*]}"; fi
    # (macOS bash 3.2 with set -u: an empty array is expanded as ${a[@]+"${a[@]}"}.)
    proxy=(); [[ $attempt -le ${BEAST_INSTALL_ANDROID_FAIL_ATTEMPTS:-0} ]] && proxy=(--proxy=http --proxy_host=127.0.0.1 --proxy_port=9)
    yes 2>/dev/null | "$sdkmanager" --sdk_root="$dir" ${proxy[@]+"${proxy[@]}"} --install "${packages[@]}" 2>&1 | grep -v '^\[=*' || true
    missing=(); missing_markers=()
    for i in "${!packages[@]}"; do
        [[ -e "$dir/${markers[$i]}" ]] || { missing+=("${packages[$i]}"); missing_markers+=("${markers[$i]}"); }
    done
    packages=(${missing[@]+"${missing[@]}"}); markers=(${missing_markers[@]+"${missing_markers[@]}"})
    [[ ${#packages[@]} -eq 0 ]] && break
    if [[ $attempt -ge 3 ]]; then
        echo "sdkmanager did not install ${packages[*]} after 3 attempts ($dir/${markers[0]} is missing)."
        echo "The cause is most likely the network: check the connection to dl.google.com (DNS, proxy, firewall) and run the script again."
        exit 1
    fi
    pause=$((attempt * 5))
    echo "Not installed: ${packages[*]} (probably a network error reaching dl.google.com); retrying in $pause s..."
    sleep "$pause"
done

# --- AVD -----------------------------------------------------------------------------------------
if [[ $emulator == 1 ]]; then
    avdmanager="$tools_dir/bin/avdmanager"
    # Both variables: a stale ANDROID_SDK_ROOT (another SDK) would make avdmanager ignore this one.
    export ANDROID_HOME="$dir" ANDROID_SDK_ROOT="$dir"
    # One AVD directory for avdmanager and the emulator: their defaults differ between versions
    # (~/.android/avd or ~/.config/.android/avd); the profile below exports it too.
    export ANDROID_AVD_HOME="${ANDROID_AVD_HOME:-$HOME/.android/avd}"
    mkdir -p "$ANDROID_AVD_HOME"
    # The emulator's own list ('run --avd' starts the AVD through it; avdmanager's list is less reliable).
    "$dir/emulator/emulator" -list-avds >"$work/avds.txt" 2>/dev/null || true
    if [[ $force == 1 ]] || ! grep -qx "$AVD" "$work/avds.txt"; then
        echo no | "$avdmanager" create avd --force -n "$AVD" -k "$image" -d pixel_6 >/dev/null
        echo "AVD created: $AVD ($image)"
    else
        echo "Already created: AVD $AVD"
    fi
fi

# --- Environment ---------------------------------------------------------------------------------
if [[ $profile == 1 ]]; then
    case "${SHELL:-}" in
        */zsh) rc="$HOME/.zshrc" ;;
        */bash) [[ $os == mac ]] && rc="$HOME/.bash_profile" || rc="$HOME/.bashrc" ;;
        *) rc="$HOME/.profile" ;;
    esac
    line="export ANDROID_HOME=\"$dir\"; export JAVA_HOME=\"$jdk\"; export PATH=\"\$ANDROID_HOME/platform-tools:\$PATH\""
    [[ $emulator == 1 ]] && line="$line; export ANDROID_AVD_HOME=\"$ANDROID_AVD_HOME\""
    if ! grep -qsF "$line" "$rc"; then
        printf '\n# Android SDK and JDK (added by Beast InstallAndroid.sh)\n%s\n' "$line" >>"$rc"
        echo "Added ANDROID_HOME, JAVA_HOME and platform-tools to $rc (open a new terminal)."
    fi
fi
echo
echo "Beast uses (defaults from ANDROID_HOME and JAVA_HOME$([[ $profile == 0 ]] && echo '; set them or Config/BuildConfig.json'):"
echo "  android.sdkRoot  = $dir"
echo "  android.ndkRoot  = $dir/ndk   (newest; this script installed $ndk)"
echo "  android.javaHome = $jdk"
[[ $emulator == 1 ]] && echo "  android.defaultAvd = $AVD   (ANDROID_AVD_HOME=$ANDROID_AVD_HOME)"
[[ $profile == 0 ]] && printf '\nConfig/BuildConfig.json:\n  "android": { "sdkRoot": "%s", "ndkRoot": "%s/ndk", "javaHome": "%s"%s }\n' "$dir" "$dir" "$jdk" "$([[ $emulator == 1 ]] && echo ", \"defaultAvd\": \"$AVD\"")"
exit 0
