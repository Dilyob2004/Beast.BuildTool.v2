#!/usr/bin/env bash
# Android on a running emulator or device (adb): builds the 'beast new' template APK, installs and
# starts it (run), checks its logcat greeting, then runs native test executables (test).
#   Tests/AndroidDevice.sh          needs ANDROID_HOME, an NDK (ANDROID_NDK_HOME/ANDROID_NDK_LATEST_HOME
#                                   or $ANDROID_HOME/ndk), JAVA_HOME and a device in 'adb devices'
#   BEAST=/path/to/tool             tool to test (default: BuildTool/bin/Beast.BuildTool)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
if [[ -n "${BEAST:-}" ]]; then read -r -a tool <<<"$BEAST"
elif [[ -x "$root/BuildTool/bin/Beast.BuildTool" ]]; then tool=("$root/BuildTool/bin/Beast.BuildTool")
else tool=(dotnet "$root/BuildTool/bin/Beast.BuildTool.dll"); fi
sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
ndk="${ANDROID_NDK_HOME:-${ANDROID_NDK_LATEST_HOME:-$sdk/ndk}}"
adb="$sdk/platform-tools/adb"
work="$here/Generated/AndroidDevice-$(date +%s)-$$"
project="$work/Droid"
mkdir -p "$work"
fail() {
    echo "FAIL: $1"; [[ -f "${2:-}" ]] && cat "$2"
    if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
        local text="$1"; [[ -f "${2:-}" ]] && text+=$'\n'"$(grep -v ' Verbose ' "$2" | tail -n 40)"
        text="${text//'%'/'%25'}"; text="${text//$'\r'/}"; text="${text//$'\n'/'%0A'}"
        echo "::error title=AndroidDevice.sh::$text"
    fi
    exit 1
}
beast() { "${tool[@]}" "$@" >"$work/last.log" 2>&1 || fail "beast $*" "$work/last.log"; }

[[ -d "$sdk" && -d "$ndk" ]] || fail "ANDROID_HOME and an NDK are required"
[[ "$("$adb" get-state 2>/dev/null)" == device ]] || fail "no device or emulator in 'adb devices'"
abi="$("$adb" shell getprop ro.product.cpu.abi | tr -d '\r')"
echo "Device ABI: $abi"

beast new Droid --path "$work"
python3 - "$project/Config/BuildConfig.json" "$sdk" "$ndk" "${JAVA_HOME:-}" "$abi" <<'EOF'
import json, sys
path, sdk, ndk, java, abi = sys.argv[1:]
c = json.load(open(path))
c["android"].update({"sdkRoot": sdk, "ndkRoot": ndk, "javaHome": java, "abis": [abi]})
json.dump(c, open(path, "w"), indent=2)
EOF
# Native tests: one passes (with its arguments), one fails with exit code 3.
mkdir -p "$project/Source/DroidTests" "$project/Source/DroidFailing"
cat >"$project/Source/DroidTests/DroidTests.Build.cs" <<'EOF'
using Beast.BuildTool;
public sealed class DroidTests : ModuleRules { public DroidTests(ModulesInfo info) { } }
EOF
cat >"$project/Source/DroidTests/Tests.cpp" <<'EOF'
#include <cstdio>
#include <cstring>
#include <vector>
int main(int argc, char** argv) { std::vector<int> v(3, 14); int s = 0; for (int x : v) s += x; std::printf("droid tests %d\n", s); return s == 42 && argc == 2 && std::strcmp(argv[1], "--ok") == 0 ? 0 : 1; }
EOF
cat >"$project/Source/DroidFailing/DroidFailing.Build.cs" <<'EOF'
using Beast.BuildTool;
public sealed class DroidFailing : ModuleRules { public DroidFailing(ModulesInfo info) { } }
EOF
echo 'int main() { return 3; }' >"$project/Source/DroidFailing/Failing.cpp"
cat >"$project/Source/DroidTests.Target.cs" <<'EOF'
using Beast.BuildTool;
public sealed class DroidTestsTarget : TargetRules { public DroidTestsTarget(ModulesInfo info) { Modules.Add("DroidTests"); IsTest = true; TestArguments.Add("--ok"); } }
EOF
cat >"$project/Source/DroidFailing.Target.cs" <<'EOF'
using Beast.BuildTool;
public sealed class DroidFailingTarget : TargetRules { public DroidFailingTarget(ModulesInfo info) { Modules.Add("DroidFailing"); IsTest = true; } }
EOF

# run: build the APK, install it and start the activity; the template logs its greeting.
"$adb" logcat -c || true
"${tool[@]}" run "$project" --platform Android --target Droid --config Debug >"$work/run.log" 2>&1 || fail "beast run --platform Android" "$work/run.log"
for _ in $(seq 1 30); do "$adb" logcat -d -s Droid:I >"$work/logcat.txt" 2>&1 || true; grep -q "Hello from Droid!" "$work/logcat.txt" && break; sleep 1; done
grep -q "Hello from Droid!" "$work/logcat.txt" || fail "no 'Hello from Droid!' in logcat" "$work/logcat.txt"
echo "PASS: APK installed and started, greeting in logcat"

# test: executables pushed with adb and run on the device; the failing one fails the command.
beast test "$project" --platform Android --target DroidTests
if "${tool[@]}" test "$project" --platform Android --target DroidTests,DroidFailing >"$work/test.log" 2>&1; then fail "failing Android test passed" "$work/test.log"; fi
grep -q "FAIL DroidFailing" "$work/test.log" || fail "failing test not reported" "$work/test.log"
echo "PASS: native tests on the device (pass and fail)"
[[ -n "${GITHUB_ACTIONS:-}" ]] && echo "::notice title=AndroidDevice.sh::run and test passed on $abi"
echo "PASS: Android device tests. Projects: $work"
