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
#   bash InstallAndroid.sh --emulator         also the emulator, a system image and AVD beast_api34
#   bash InstallAndroid.sh --force            download again what is already installed (including the JDK)
#   bash InstallAndroid.sh --no-profile       do not edit ~/.zshrc / ~/.bashrc / ~/.profile
# Licenses are accepted automatically (sdkmanager --licenses). Sources: dl.google.com (repository
# catalogue, SHA-1 checked) and api.adoptium.net (Temurin, SHA-256 checked).
set -euo pipefail
# In GitHub Actions a failure is also an annotation: the failing command and its line.
[[ -n "${GITHUB_ACTIONS:-}" ]] && trap 'echo "::error title=InstallAndroid.sh::line $LINENO: $BASH_COMMAND failed"' ERR
NDK_DEFAULT=27.3.13750724
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

dir="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$sdk_default}}"; jdk_dir="$jdk_default"; ndk="$NDK_DEFAULT"
emulator=0; force=0; profile=1
while [[ $# -gt 0 ]]; do
    case "$1" in
        --dir) dir="$2"; shift 2 ;;
        --jdk-dir) jdk_dir="$2"; shift 2 ;;
        --ndk) ndk="$2"; shift 2 ;;
        --emulator) emulator=1; shift ;;
        --force) force=1; shift ;;
        --no-profile) profile=0; shift ;;
        -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done
mkdir -p "$dir"; dir="$(cd "$dir" && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/beast-android.XXXXXX")"
trap 'rm -rf "$work"' EXIT

download() { echo "Downloading $1"; curl -fL --retry 3 --progress-bar -o "$2" "$1"; }
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
sdkmanager="$dir/cmdline-tools/latest/bin/sdkmanager"
if [[ $force == 1 || ! -x "$sdkmanager" ]]; then
    # The stable cmdline-tools;latest archive for this OS from Google's repository catalogue.
    download "$REPOSITORY/repository2-3.xml" "$work/repository.xml"
    [[ $os == mac ]] && host=macosx || host=linux
    read -r file checksum < <(awk -v host="$host" -v arch="$arch" '
        /<remotePackage path="cmdline-tools;latest">/ { inside = 1; stable = 0; next }
        inside && /<\/remotePackage>/ { inside = 0; next }
        inside && /<channelRef ref="channel-0"\/>/ { stable = 1 }
        inside && stable && /<archive>/ { u = ""; c = ""; o = ""; a = "" }
        inside && stable { if (match($0, /<url>[^<]+<\/url>/)) u = substr($0, RSTART + 5, RLENGTH - 11)
                           if (match($0, /<checksum[^>]*>[^<]+<\/checksum>/)) { s = substr($0, RSTART, RLENGTH); sub(/<checksum[^>]*>/, "", s); sub(/<\/checksum>/, "", s); c = s }
                           if (match($0, /<host-os>[^<]+<\/host-os>/)) o = substr($0, RSTART + 9, RLENGTH - 19)
                           if (match($0, /<host-arch>[^<]+<\/host-arch>/)) a = substr($0, RSTART + 11, RLENGTH - 23) }
        inside && stable && /<\/archive>/ && o == host { if (a == arch || (a == "" && best == "")) best = u " " c; if (a == arch) exact = u " " c }
        END { print (exact != "" ? exact : best) }' "$work/repository.xml")
    [[ -n "${file:-}" ]] || { echo "cmdline-tools for $host not found in $REPOSITORY/repository2-3.xml"; exit 1; }
    download "$REPOSITORY/$file" "$work/cmdline-tools.zip"
    [[ "$(sha 1 "$work/cmdline-tools.zip")" == "$checksum" ]] || { echo "cmdline-tools checksum mismatch ($file)"; exit 1; }
    mkdir -p "$work/cmdline-tools" && unzip -q "$work/cmdline-tools.zip" -d "$work/cmdline-tools"
    rm -rf "$dir/cmdline-tools/latest"; mkdir -p "$dir/cmdline-tools"
    mv "$work/cmdline-tools/cmdline-tools" "$dir/cmdline-tools/latest"
    echo "Command-line tools installed: $dir/cmdline-tools/latest ($file)"
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
yes 2>/dev/null | "$sdkmanager" --sdk_root="$dir" --licenses >/dev/null || true
if [[ ${#packages[@]} -gt 0 ]]; then
    echo "Installing: ${packages[*]}"
    yes 2>/dev/null | "$sdkmanager" --sdk_root="$dir" --install "${packages[@]}" | grep -v '^\[=*' || true
    for i in "${!packages[@]}"; do
        [[ -e "$dir/${markers[$i]}" ]] || { echo "sdkmanager did not install ${packages[$i]} ($dir/${markers[$i]} is missing)"; exit 1; }
    done
fi

# --- AVD -----------------------------------------------------------------------------------------
if [[ $emulator == 1 ]]; then
    avdmanager="$dir/cmdline-tools/latest/bin/avdmanager"
    # Both variables: a stale ANDROID_SDK_ROOT (another SDK) would make avdmanager ignore this one.
    export ANDROID_HOME="$dir" ANDROID_SDK_ROOT="$dir"
    if [[ $force == 1 ]] || ! "$avdmanager" list avd -c 2>/dev/null | grep -qx "$AVD"; then
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
[[ $emulator == 1 ]] && echo "  android.defaultAvd = $AVD"
[[ $profile == 0 ]] && printf '\nConfig/BuildConfig.json:\n  "android": { "sdkRoot": "%s", "ndkRoot": "%s/ndk", "javaHome": "%s"%s }\n' "$dir" "$dir" "$jdk" "$([[ $emulator == 1 ]] && echo ", \"defaultAvd\": \"$AVD\"")"
exit 0
