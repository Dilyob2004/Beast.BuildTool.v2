#!/usr/bin/env bash
# Linux and macOS integration tests: builds and runs real programs with the host toolchain
# (Linux: Clang and, if installed, GCC; macOS: Apple clang).
#   Tests/Unix.sh            all checks
#   Tests/Unix.sh --android  also builds the Game APK with the Android SDK/NDK (ANDROID_HOME,
#                            ANDROID_NDK_HOME or $ANDROID_HOME/ndk, JAVA_HOME)
#   BEAST=/path/to/tool      tool to test (default: BuildTool/bin/Beast.BuildTool, or the .dll via dotnet)
# Projects and logs stay in Tests/Generated. Exit code 0 = all passed.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
if [[ -n "${BEAST:-}" ]]; then read -r -a tool <<<"$BEAST"
elif [[ -x "$root/BuildTool/bin/Beast.BuildTool" ]]; then tool=("$root/BuildTool/bin/Beast.BuildTool")
else tool=(dotnet "$root/BuildTool/bin/Beast.BuildTool.dll"); fi
case "$(uname -s)" in Darwin) os=Mac; platform=Mac; so=dylib;; *) os=Linux; platform=Linux; so=so;; esac
[[ "$(uname -m)" =~ ^(arm64|aarch64)$ && $os == Linux ]] && platform=LinuxArm64
[[ "$(uname -m)" == x86_64 && $os == Mac ]] && platform=MacX64
work="$here/Generated/Unix-$(date +%s)-$$"
project="$work/Demo"
passed=0; skipped=()
# In GitHub Actions a failure is also reported as an annotation (with the end of the log), so it
# can be read without opening the job log.
annotate() {
    [[ -n "${GITHUB_ACTIONS:-}" ]] || return 0
    local text="$1"
    [[ -n "${2:-}" && -f "$2" ]] && text+=$'\n'"$(grep -v ' Verbose ' "$2" | tail -n 40)"
    text="${text//'%'/'%25'}"; text="${text//$'\r'/}"; text="${text//$'\n'/'%0A'}"
    echo "::error title=Unix.sh::$text"
}
beast() { "${tool[@]}" "$@" >"$work/last.log" 2>&1 || { cat "$work/last.log"; echo "FAIL: beast $*"; annotate "beast $* failed" "$work/last.log"; exit 1; }; }
check() { if ! eval "$1"; then echo "FAIL: $2"; annotate "$2" "$work/last.log"; exit 1; fi; }
pass() { echo "PASS: $1"; passed=$((passed + 1)); }
put() { mkdir -p "$(dirname "$project/$1")"; printf '%s\n' "$2" >"$project/$1"; }
module() { put "Source/$1/$1.Build.cs" "using Beast.BuildTool;
public sealed class $1 : ModuleRules { public $1(ModulesInfo info) { $2 } }"; }
target() { put "Source/$1.Target.cs" "using Beast.BuildTool;
public sealed class ${1}Target : TargetRules { public ${1}Target(ModulesInfo info) { $2 } }"; }
config() { python3 - "$project/Config/BuildConfig.json" "$1" <<'EOF'
import json, sys
path, change = sys.argv[1], sys.argv[2]
c = json.load(open(path))
exec(change)
json.dump(c, open(path, "w"), indent=2)
EOF
}
bin() { echo "$project/Binaries/$platform/$1"; }
mtime() { if [[ $os == Mac ]]; then stat -f %m "$1"; else stat -c %Y "$1"; fi; }

# Apple clang does not implement C++20 named modules: the main run builds without them, and a later
# group repeats the module build with Homebrew LLVM (mac.cxx) when it is installed.
modules=1
if [[ $os == Mac ]]; then modules=0; export DEMO_MODULES=0; fi
mkdir -p "$work"
beast new Demo --path "$work"
rm -rf "$project/Source"
# Engine (shared library: Core), Tools (static library: Util), Game (Launch, uses both).
module Core 'PublicIncludePaths.Add("Public"); PublicDefinitions.Add("CORE_ANSWER=42");'
put Source/Core/Public/Core.h '#pragma once
CORE_API int CoreAnswer();
CORE_API int PlatformTag();'
put Source/Core/Core.cpp '#include "Core.h"
int CoreAnswer() { return CORE_ANSWER; }'
put Source/Core/Linux/CoreLinux.cpp '#include "Core.h"
int PlatformTag() { return 1; }'
put Source/Core/Mac/CoreMac.cpp '#include "Core.h"
int PlatformTag() { return 2; }'
put Source/Core/IOS/CoreIOS.cpp '#include "Core.h"
int PlatformTag() { return 4; }'
put Source/Core/Windows/CoreWindows.cpp '#error Windows sources must not be compiled here'
put Source/Core/Android/CoreAndroid.cpp '#include "Core.h"
#if !defined(__ANDROID__)
#error Android sources must not be compiled here
#endif
int PlatformTag() { return 3; }'
module Util 'PublicIncludePaths.Add("Public"); PrecompiledHeaderSource = "Private/UtilPch.h"; UseUnityBuild = true; UnityBatchSize = 2;'
put Source/Util/Public/Util.h '#pragma once
int Twice(int value);
extern "C" int Asm40();'
put Source/Util/Private/UtilPch.h '#pragma once
#include <string>
#include <vector>'
put Source/Util/Private/A.cpp '#include "UtilPch.h"
#include "Util.h"
int Twice(int value) { std::vector<int> v{value, value}; return v[0] + v[1]; }'
put Source/Util/Private/B.cpp '#include "UtilPch.h"
static std::string Name() { return "util"; }
int NameLength() { return static_cast<int>(Name().size()); }'
put Source/Util/Private/Number.S '#if defined(__APPLE__)
#define SYMBOL(name) _##name
#else
#define SYMBOL(name) name
#endif
    .text
    .globl SYMBOL(Asm40)
SYMBOL(Asm40):
#if defined(__x86_64__)
    movl $40, %eax
    ret
#elif defined(__aarch64__)
    mov w0, #40
    ret
#endif
#if defined(__linux__)
    .section .note.GNU-stack,"",@progbits
#endif'
# C++20 modules: an interface with a partition interface, an internal partition and an
# implementation unit, built by the Tools target and imported by Game.
module Geometry ''
put Source/Geometry/Geometry.cppm 'module;
#include <cstdio>
export module Geometry;
export import :Shapes;
import :Detail;
export int Area(int w, int h);
export int Scaled(int v) { return v * Factor(); }'
put Source/Geometry/Shapes.cppm 'export module Geometry:Shapes;
export struct Rect { int w, h; };'
put Source/Geometry/Detail.cpp 'module Geometry:Detail;
int Factor() { return 2; }'
put Source/Geometry/Area.cpp 'module Geometry;
int Area(int w, int h) { Rect r{w, h}; return r.w * r.h; }'
# DEMO_MODULES=0 (Apple clang) builds the same program without modules: ModuleAnswer() then
# comes from NoModules/Answer.cpp instead of Modules/Answer.cpp, which imports Geometry.
modules_enabled='System.Environment.GetEnvironmentVariable("DEMO_MODULES") != "0"'
module Launch "PrivateDependencies.Add(\"Core\"); PrivateDependencies.Add(\"Util\");
    if ($modules_enabled) { PrivateDependencies.Add(\"Geometry\"); ExcludedSources.Add(\"NoModules\"); } else ExcludedSources.Add(\"Modules\");"
put Source/Launch/Main.cpp '#include <cstdio>
#include "Core.h"
#include "Util.h"
int ModuleAnswer();
int main() { std::printf("%d %d %d %d\n", CoreAnswer(), Twice(21), Asm40() + 2, ModuleAnswer()); return PlatformTag() == 0; }'
put Source/Launch/Modules/Answer.cpp 'import Geometry;
int ModuleAnswer() { return Area(3, 7) * Scaled(1); }'
put Source/Launch/NoModules/Answer.cpp 'int ModuleAnswer() { return 42; }'
target Engine 'Type = TargetType.SharedLibrary; Modules.Add("Core"); Version = "1.2.0";'
target Tools "Type = TargetType.StaticLibrary; Modules.Add(\"Util\"); if ($modules_enabled) Modules.Add(\"Geometry\");"
target Game 'Modules.Add("Launch"); TargetDependencies.Add("Engine"); TargetDependencies.Add("Tools"); LinkTimeOptimization = true;'
module TestsMain 'PrivateDependencies.Add("Core");'
put Source/TestsMain/Tests.cpp '#include <cstring>
#include "Core.h"
int main(int argc, char** argv) { return CoreAnswer() == 42 && argc == 2 && std::strcmp(argv[1], "--ok") == 0 ? 0 : 1; }'
target CoreTests 'Modules.Add("TestsMain"); TargetDependencies.Add("Engine"); IsTest = true; TestArguments.Add("--ok");'
module FailingMain ''
put Source/FailingMain/Failing.cpp 'int main() { return 3; }'
target FailingTests 'Modules.Add("FailingMain"); IsTest = true;'
module SanMain ''
put Source/SanMain/San.cpp '#include <cstdio>
#include <vector>
int main() { std::vector<int> v(16, 1); int s = 0; for (int x : v) s += x; std::printf("san %d\n", s); return 0; }'
target Sanitized 'Modules.Add("SanMain"); Sanitizers = Sanitizer.Address | Sanitizer.Undefined;'
# A windowed application: an .app bundle on macOS, a plain program on Linux.
module ViewerMain 'PrivateDependencies.Add("Core");'
put Source/ViewerMain/Viewer.cpp '#include <cstdio>
#include "Core.h"
int main() { std::printf("viewer %d\n", CoreAnswer()); return 0; }'
target Viewer 'Modules.Add("ViewerMain"); TargetDependencies.Add("Engine"); ConsoleApplication = false; Version = "2.1";'

# Builds and runs Game for both configurations.
for configuration in Debug Release; do
    beast build "$project" --target Game --config "$configuration"
    out="$(bin "$configuration")"
    check '[[ -f "$out/libEngine.$so" && -f "$out/libTools.a" && -x "$out/Game" ]]' "products missing ($configuration)"
    check '[[ "$("$out/Game")" == "42 42 42 42" ]]' "wrong Game output ($configuration)"
done
out="$(bin Debug)"
if [[ $os == Linux ]]; then
    check 'readelf -d "$out/Game" | grep -q "RUNPATH.*\$ORIGIN"' 'Game has no $ORIGIN RUNPATH'
    check 'readelf -d "$out/Game" | grep -q "NEEDED.*libEngine.so"' 'Game does not need libEngine.so'
    check 'readelf -d "$out/libEngine.so" | grep -q "SONAME.*libEngine.so"' 'libEngine.so has no SONAME'
else
    check 'otool -l "$out/Game" | grep -q "@loader_path"' 'Game has no @loader_path rpath'
    check 'otool -L "$out/Game" | grep -q "@rpath/libEngine.dylib"' 'Game does not load @rpath/libEngine.dylib'
    check '[[ -d "$(bin Release)/Game.dSYM" ]]' 'Release has no dSYM'
fi
check 'grep -q -- "-flto" "$project/compile_commands.json"' 'LTO flag missing from Release commands'
pass "targets, shared/static libraries, platform directories, PCH, Unity, .S,$([[ $modules == 1 ]] && echo ' C++20 modules,') LTO, rpath"

# No-op build relinks nothing; a header edit recompiles its users only.
stamp="$(mtime "$out/Game")"; sleep 1
beast build "$project" --target Game --config Debug
check '[[ "$(mtime "$out/Game")" == "$stamp" ]]' 'no-op build relinked Game'
sed -i.bak 's/CORE_API int PlatformTag();/CORE_API int PlatformTag(); \/\/ edited/' "$project/Source/Core/Public/Core.h"
beast build "$project" --target Game --config Debug
check 'grep -q "Compiling Main.cpp" "$work/last.log" && ! grep -q "Compiling A.cpp\|Compiling Module_" "$work/last.log"' 'wrong rebuild set after header edit'
# Modules: an implementation unit edit recompiles only itself; an interface edit, its importers.
if [[ $modules == 1 ]]; then
sed -i.bak 's/r.w \* r.h;/r.w * r.h; \/* edited *\//' "$project/Source/Geometry/Area.cpp"
beast build "$project" --target Game --config Debug
check 'grep -q "Compiling Area.cpp" "$work/last.log" && ! grep -q "Compiling Answer.cpp\|Compiling Geometry.cppm" "$work/last.log"' 'implementation unit edit rebuilt too much'
sed -i.bak 's/export int Area(int w, int h);/export int Area(int w, int h); \/\/ edited/' "$project/Source/Geometry/Geometry.cppm"
beast build "$project" --target Game --config Debug
check 'grep -q "Compiling Geometry.cppm" "$work/last.log" && grep -q "Compiling Answer.cpp" "$work/last.log" && ! grep -q "Compiling Main.cpp" "$work/last.log"' 'interface edit did not rebuild exactly its importers'
check '[[ "$("$out/Game")" == "42 42 42 42" ]]' 'Game output after module edits'
fi
pass 'incremental build (headers'"$([[ $modules == 1 ]] && echo ', C++20 modules')"')'

# run, test
"${tool[@]}" run "$project" --target Game --no-build >"$work/run.log" 2>&1 || { cat "$work/run.log"; echo 'FAIL: run'; exit 1; }
check 'grep -q "42 42 42 42" "$work/run.log"' 'run did not start Game'
beast test "$project" --target CoreTests
if "${tool[@]}" test "$project" --target CoreTests,FailingTests >"$work/test.log" 2>&1; then echo 'FAIL: failing test passed'; exit 1; fi
check 'grep -q "FAIL FailingTests" "$work/test.log"' 'failing test not reported'
pass 'run, test'

# install/package: bin/Game finds lib/libEngine through its rpath.
prefix="$work/Install"
beast install "$project" --prefix "$prefix" --target Game,Engine,Tools
for file in bin/Game "lib/libEngine.$so" lib/libTools.a include/Core.h include/Util.h; do
    check '[[ -e "$prefix/$file" ]]' "install: $file missing"
done
check '[[ "$("$prefix/bin/Game")" == "42 42 42 42" ]]' 'installed Game does not run'
beast package "$project" --target Game,Engine
check '[[ -f "$project/Binaries/Packages/Demo-$platform-Release.zip" ]]' 'package zip missing'
pass 'install, package'

# Installed libraries are usable from CMake (find_package) and pkg-config.
mkdir -p "$work/Consumer"
printf '%s\n' '#include <cstdio>' '#include "Core.h"' '#include "Util.h"' \
    'int main() { std::printf("%d %d\n", CoreAnswer(), Twice(21)); return 0; }' >"$work/Consumer/main.cpp"
printf '%s\n' 'cmake_minimum_required(VERSION 3.16)' 'project(Consumer CXX)' 'find_package(Engine 1.0 CONFIG REQUIRED)' \
    'find_package(Tools CONFIG REQUIRED)' 'add_executable(consumer main.cpp)' 'target_link_libraries(consumer PRIVATE Engine::Engine Tools::Tools)' >"$work/Consumer/CMakeLists.txt"
if command -v cmake >/dev/null; then
    cmake -S "$work/Consumer" -B "$work/Consumer/build" -DCMAKE_PREFIX_PATH="$prefix" -DCMAKE_BUILD_TYPE=Release >"$work/cmake.log" 2>&1 &&
        cmake --build "$work/Consumer/build" >>"$work/cmake.log" 2>&1 || { cat "$work/cmake.log"; echo 'FAIL: CMake consumer'; exit 1; }
    check '[[ "$("$work/Consumer/build/consumer")" == "42 42" ]]' 'CMake consumer output'
    sed -i.bak 's/Engine 1.0/Engine 2.0/' "$work/Consumer/CMakeLists.txt"
    if cmake -S "$work/Consumer" -B "$work/Consumer/build2" -DCMAKE_PREFIX_PATH="$prefix" >/dev/null 2>&1; then
        echo 'FAIL: Engine 1.2.0 accepted for find_package(Engine 2.0)'; exit 1
    fi
    pass 'CMake package (find_package, version)'
else skipped+=('CMake consumer'); fi
if command -v pkg-config >/dev/null; then
    PKG_CONFIG_PATH="$prefix/lib/pkgconfig" c++ "$work/Consumer/main.cpp" -o "$work/Consumer/pc" $(PKG_CONFIG_PATH="$prefix/lib/pkgconfig" pkg-config --cflags --libs Engine Tools) ||
        { echo 'FAIL: pkg-config consumer'; exit 1; }
    check '[[ "$("$work/Consumer/pc")" == "42 42" ]]' 'pkg-config consumer output'
    check '[[ "$(PKG_CONFIG_PATH="$prefix/lib/pkgconfig" pkg-config --modversion Engine)" == "1.2.0" ]]' 'pkg-config version'
    pass 'pkg-config'
else skipped+=('pkg-config'); fi

# Windowed application: Viewer.app on macOS (Info.plist, signed when installed).
beast build "$project" --target Viewer --config Release
"${tool[@]}" run "$project" --target Viewer --config Release --no-build >"$work/viewer.log" 2>&1 || { cat "$work/viewer.log"; echo 'FAIL: run Viewer'; exit 1; }
check 'grep -q "viewer 42" "$work/viewer.log"' 'Viewer output'
if [[ $os == Mac ]]; then
    app="$(bin Release)/Viewer.app"
    check '[[ -x "$app/Contents/MacOS/Viewer" ]] && grep -q "<string>2.1</string>" "$app/Contents/Info.plist"' 'Viewer.app bundle'
    check '[[ -d "$(bin Release)/Viewer.app.dSYM" ]]' 'Viewer.app.dSYM missing'
    beast install "$project" --prefix "$work/Bundle" --target Viewer,Engine
    check 'codesign --verify --deep "$work/Bundle/bin/Viewer.app"' 'installed bundle is not signed'
    check '[[ "$("$work/Bundle/bin/Viewer.app/Contents/MacOS/Viewer")" == "viewer 42" ]]' 'installed bundle does not run'
    pass 'macOS application bundle'
else
    check '[[ -x "$(bin Release)/Viewer" ]]' 'Viewer program missing'
    pass 'windowed application (plain program on Linux)'
fi

# macOS universal binaries (lipo) and a disk image with the app (dylibs embedded in Frameworks).
if [[ $os == Mac ]]; then
    beast build "$project" --target Viewer --arch universal --config Release
    uni="$project/Binaries/MacUniversal/Release"
    check 'lipo -archs "$uni/Viewer.app/Contents/MacOS/Viewer" | grep -q x86_64 && lipo -archs "$uni/Viewer.app/Contents/MacOS/Viewer" | grep -q arm64' 'Viewer is not universal'
    check 'lipo -archs "$uni/libEngine.dylib" | grep -q x86_64' 'libEngine.dylib is not universal'
    check '[[ "$("$uni/Viewer.app/Contents/MacOS/Viewer")" == "viewer 42" ]]' 'universal Viewer does not run'
    check '[[ "$(arch -x86_64 "$uni/Viewer.app/Contents/MacOS/Viewer")" == "viewer 42" ]]' 'x86_64 slice does not run (Rosetta)'
    beast package "$project" --target Viewer,Engine --arch universal --dmg
    dmg="$project/Binaries/Packages/Demo-MacUniversal-Release.dmg"
    # hdiutil verify sometimes fails transiently on CI runners (resource busy): up to three tries.
    check '[[ -f "$dmg" ]] && { hdiutil verify "$dmg" >/dev/null 2>&1 || { sleep 3; hdiutil verify "$dmg" >/dev/null 2>&1; } || { sleep 5; hdiutil verify "$dmg" >"$work/last.log" 2>&1; }; }' 'disk image missing or invalid'
    mount="$work/dmg"; mkdir -p "$mount"
    hdiutil attach -nobrowse -readonly -mountpoint "$mount" "$dmg" >/dev/null
    ok=1
    [[ -f "$mount/Viewer.app/Contents/Frameworks/libEngine.dylib" && -L "$mount/Applications" ]] || ok=0
    [[ "$("$mount/Viewer.app/Contents/MacOS/Viewer" 2>&1)" == "viewer 42" ]] || ok=0
    codesign --verify --deep "$mount/Viewer.app" || ok=0
    hdiutil detach "$mount" >/dev/null || true
    check '[[ $ok == 1 ]]' 'disk image content (app with embedded dylib, Applications link, signature)'
    pass 'macOS universal binaries and disk image'
fi

# Sanitizers (Debug), when the compiler's sanitizer runtime is installed (Linux: libclang-rt-dev).
cxx=clang++; [[ $os == Mac ]] && cxx="xcrun clang++"
if command -v ${cxx%% *} >/dev/null && printf 'int main(){return 0;}' | $cxx -fsanitize=address,undefined -x c++ - -o "$work/sanitizer-probe" 2>/dev/null; then
    beast build "$project" --target Sanitized --config Debug
    check '[[ "$("$(bin Debug)/Sanitized")" == "san 16" ]]' 'sanitized program failed'
    pass 'AddressSanitizer + UndefinedBehaviorSanitizer'
else skipped+=('sanitizers (no runtime)'); fi

# GCC (Linux): the same project with linux.compiler = gcc.
if [[ $os == Linux ]] && command -v g++ >/dev/null; then
    config 'c.setdefault("linux", {})["compiler"] = "gcc"'
    beast clean "$project"
    for configuration in Debug Release; do
        beast build "$project" --target Game --config "$configuration"
        check '[[ "$("$(bin "$configuration")/Game")" == "42 42 42 42" ]]' "GCC Game output ($configuration)"
    done
    check 'grep -q "bin/g++" "$project/compile_commands.json"' 'GCC was not used'
    config 'c["linux"]["compiler"] = ""'
    pass "GCC (PCH,$([[ $modules == 1 ]] && echo ' C++20 modules,') LTO)"
else skipped+=('GCC'); fi

# macOS: C++20 modules with Homebrew LLVM (Apple clang has none).
if [[ $os == Mac ]]; then
    llvm="$(brew --prefix llvm 2>/dev/null || true)/bin/clang++"
    if [[ -x "$llvm" ]]; then
        config "c.setdefault('mac', {})['cxx'] = '$llvm'"
        export DEMO_MODULES=1
        beast build "$project" --target Game --config Debug
        check '[[ "$("$(bin Debug)/Game")" == "42 42 42 42" ]]' 'Game with modules (Homebrew LLVM)'
        check 'grep -q "\.pcm" "$work/last.log" || ls "$project"/Intermediate/Build/$platform/Debug/Tools/Modules/*.pcm >/dev/null' 'no BMIs were written'
        export DEMO_MODULES=0
        config "c['mac']['cxx'] = ''"
        pass 'C++20 modules on macOS (Homebrew LLVM)'
    else skipped+=('C++20 modules on macOS (no Homebrew LLVM)'); fi
fi

# iOS Simulator (macOS with Xcode): Game.app with Engine embedded in Frameworks, tests via simctl.
if [[ $os == Mac ]] && xcrun --sdk iphonesimulator --show-sdk-path >/dev/null 2>&1 && xcrun simctl list devices available | grep -q iPhone; then
    sim=IOSSimulator; [[ "$(uname -m)" == x86_64 ]] && sim=IOSSimulatorX64
    beast build "$project" --target Game --platform IOSSimulator --config Debug
    app="$project/Binaries/$sim/Debug/Game.app"
    check '[[ -x "$app/Game" && -f "$app/Frameworks/libEngine.dylib" ]] && grep -q "<string>iPhoneSimulator</string>" "$app/Info.plist"' 'Game.app layout'
    check 'codesign --verify "$app"' 'Game.app is not signed'
    beast build "$project" --target Game --platform IOSSimulator --config Debug
    check '! grep -qE "Done: [1-9][0-9]* executed" "$work/last.log"' 'no-op iOS build did work'
    beast test "$project" --platform IOSSimulator --target CoreTests
    if "${tool[@]}" test "$project" --platform IOSSimulator --target FailingTests >"$work/ios-test.log" 2>&1; then echo 'FAIL: failing iOS test passed'; exit 1; fi
    "${tool[@]}" run "$project" --platform IOSSimulator --target Game --no-build >"$work/ios-run.log" 2>&1 || { cat "$work/ios-run.log"; annotate 'iOS run failed' "$work/ios-run.log"; exit 1; }
    if ! grep -q "42 42 42 42" "$work/ios-run.log"; then
        cat "$work/ios-run.log"; echo 'FAIL: Game output in the simulator'; annotate 'Game output in the simulator' "$work/ios-run.log"; exit 1
    fi
    pass 'iOS Simulator (app bundle, embedded dylib, signing, test, run)'
elif [[ $os == Mac ]]; then skipped+=('iOS Simulator (no Xcode iOS SDK or simulator)'); fi

# Linux arm64 on an x86_64 host: Clang (--target) and the GCC cross compiler, run through qemu-user.
if [[ $os == Linux && "$(uname -m)" == x86_64 ]] && command -v qemu-aarch64 >/dev/null && command -v aarch64-linux-gnu-g++ >/dev/null; then
    for compiler in clang gcc; do
        if [[ $compiler == gcc ]]; then config "c.setdefault('linux', {})['cxx'] = 'aarch64-linux-gnu-g++'"; beast clean "$project"; fi
        beast build "$project" --target Game --arch arm64 --config Debug
        check 'file "$project/Binaries/LinuxArm64/Debug/Game" | grep -q aarch64' "Game is not an arm64 program ($compiler)"
        "${tool[@]}" run "$project" --target Game --arch arm64 --no-build >"$work/arm64.log" 2>&1 || { cat "$work/arm64.log"; annotate "arm64 run failed ($compiler)" "$work/arm64.log"; exit 1; }
        grep -q "42 42 42 42" "$work/arm64.log" || { cat "$work/arm64.log"; annotate "arm64 Game output ($compiler)" "$work/arm64.log"; exit 1; }
        beast test "$project" --target CoreTests --arch arm64
    done
    config "c['linux']['cxx'] = ''"
    pass 'Linux arm64 cross build (Clang, GCC; run and test through qemu-aarch64)'
elif [[ $os == Linux ]]; then skipped+=('Linux arm64 (no qemu-aarch64 or aarch64-linux-gnu-g++)'); fi

# import std (Linux, Clang with libc++): a static library target exports module Text built on std,
# the program imports std.compat and Text; std is compiled once, by the library target.
if [[ $os == Linux ]] && ls /usr/lib/llvm-*/share/libc++/v1/std.cppm >/dev/null 2>&1 && command -v clang++ >/dev/null; then
    std="$work/StdDemo"
    beast new StdDemo --path "$work"
    rm -rf "$std/Source"
    project_saved="$project"; project="$std"
    config "c.setdefault('linux', {})['stdlib'] = 'libc++'"
    module Text ''
    put Source/Text/Text.cppm 'export module Text;
import std;
export std::string Greeting(std::string_view name) { return std::format("Hello, {}!", name); }'
    module App 'PrivateDependencies.Add("Text");'
    put Source/App/Main.cpp 'import std.compat;
import Text;
int main() { std::vector<int> v{20, 22}; std::printf("%s %d\n", Greeting("std").c_str(), std::ranges::fold_left(v, 0, std::plus{})); }'
    target TextLib 'Type = TargetType.StaticLibrary; Modules.Add("Text"); CppVersion = CppVersion.Latest;'
    target StdDemo 'Modules.Add("App"); TargetDependencies.Add("TextLib"); CppVersion = CppVersion.Latest;'
    project="$project_saved"
    beast build "$std" --config Debug
    check 'grep -q "Compiling std.cppm" "$work/last.log" && grep -q "Compiling std.compat.cppm" "$work/last.log"' 'standard library modules not compiled'
    check '[[ "$("$std/Binaries/$platform/Debug/StdDemo")" == "Hello, std! 42" ]]' 'import std program output'
    check '[[ $(ls "$std"/Intermediate/Build/$platform/Debug/*/Modules/std.pcm | wc -l) == 1 ]]' 'std was compiled by more than one target'
    beast build "$std" --config Debug
    check '! grep -qE "Done: [1-9][0-9]* executed" "$work/last.log"' 'no-op build recompiled modules'
    pass 'import std (Clang, libc++)'
elif [[ $os == Linux ]]; then skipped+=('import std (no libc++ std.cppm)'); fi

# Project templates from 'beast new': a console program on Linux, an AppKit .app on macOS and a
# UIKit .app in the iOS Simulator (BEAST_QUIT_AFTER_LAUNCH makes the windowed apps quit once started).
beast new Template --path "$work"
template="$work/Template"
beast build "$template" --config Debug
BEAST_QUIT_AFTER_LAUNCH=1 "${tool[@]}" run "$template" --no-build >"$work/template.log" 2>&1 || { cat "$work/template.log"; annotate 'template run failed' "$work/template.log"; exit 1; }
grep -q "Hello from Template!" "$work/template.log" || { cat "$work/template.log"; annotate 'template output' "$work/template.log"; exit 1; }
if [[ $os == Mac ]]; then
    check '[[ -x "$template/Binaries/$platform/Debug/Template.app/Contents/MacOS/Template" ]]' 'macOS template is not an .app'
    if [[ -n "${sim:-}" ]]; then
        beast build "$template" --platform IOSSimulator --config Debug
        SIMCTL_CHILD_BEAST_QUIT_AFTER_LAUNCH=1 "${tool[@]}" run "$template" --platform IOSSimulator --no-build >"$work/template-ios.log" 2>&1 ||
            { cat "$work/template-ios.log"; annotate 'iOS template run failed' "$work/template-ios.log"; exit 1; }
        if ! grep -q "Hello from Template!" "$work/template-ios.log"; then
            # Diagnostics: the simulator's system log for the app process.
            { echo '--- simulator log'; xcrun simctl spawn booted log show --style compact --last 3m --predicate 'process == "Template"' 2>&1 | grep -v "^Filtering" | tail -n 30; } >>"$work/template-ios.log"
            cat "$work/template-ios.log"; annotate 'iOS template output' "$work/template-ios.log"; exit 1
        fi
    fi
fi
pass "project template ($([[ $os == Mac ]] && echo "AppKit app${sim:+, UIKit app in the simulator}" || echo 'console program'))"

# Resources: one declaration (Resources.Add) for every platform. Linux: next to the program; macOS:
# Res.app/Contents/Resources; iOS: the bundle root. A dependency target's resources reach the
# application; BeastResources.h finds and reads them; changes are copied incrementally.
res="$work/Res"
beast new Res --path "$work"
rm -rf "$res/Source"
project_saved="$project"; project="$res"
module Data 'Resources.Add("Data", "Assets/Data");'
put Source/Data/Data/level.txt 'level 7'
put Source/Data/Stub.cpp 'int DataStub() { return 7; }'
module ResApp 'Resources.Add("Assets");'
put Source/ResApp/Assets/greeting.txt 'resource 42'
put Source/ResApp/Assets/Sub/deep.txt 'deep'
put Source/ResApp/Main.cpp '#include "BeastResources.h"
#include <cstdio>
#include <string>
static std::string Line(const char* path) { std::string text; if (!Beast::ReadResource(path, text)) return "missing"; while (!text.empty() && (text.back() == 10 || text.back() == 13)) text.pop_back(); return text; }
int main() { std::string unused; std::printf("%s|%s|%s\n", Line("Assets/greeting.txt").c_str(), Line("Assets/Data/level.txt").c_str(), Beast::ReadResource("Assets/none.txt", unused) ? "found" : "absent"); return 0; }'
target DataLib 'Type = TargetType.StaticLibrary; Modules.Add("Data");'
target Res 'Modules.Add("ResApp"); TargetDependencies.Add("DataLib"); ConsoleApplication = info.Platform != PlatformType.Mac;'
project="$project_saved"
resbin="$res/Binaries/$platform/Debug"
[[ $os == Mac ]] && resroot="$resbin/Res.app/Contents/Resources" || resroot="$resbin"
beast build "$res" --config Debug
check '[[ -f "$resroot/Assets/greeting.txt" && -f "$resroot/Assets/Sub/deep.txt" && -f "$resroot/Assets/Data/level.txt" ]]' 'resources not placed'
"${tool[@]}" run "$res" --no-build >"$work/res.log" 2>&1 || { cat "$work/res.log"; annotate 'resource program failed' "$work/res.log"; exit 1; }
check 'grep -q "resource 42|level 7|absent" "$work/res.log"' 'resources not read by BeastResources.h'
beast build "$res" --config Debug
check '! grep -qE "Done: [1-9][0-9]* executed" "$work/last.log"' 'no-op build copied resources'
printf 'resource 43\n' >"$res/Source/ResApp/Assets/greeting.txt"
rm "$res/Source/ResApp/Assets/Sub/deep.txt"
beast build "$res" --config Debug
check 'grep -q "Done: 1 executed" "$work/last.log"' 'changed resource: expected one copy'
check '[[ ! -f "$resroot/Assets/Sub/deep.txt" ]] && grep -q "resource 43" "$resroot/Assets/greeting.txt"' 'resource update/removal'
beast install "$res" --prefix "$work/ResInstall" --config Debug
if [[ $os == Mac ]]; then
    check 'codesign --verify --deep --strict "$work/ResInstall/bin/Res.app"' 'installed bundle with resources is not validly signed'
    check '"$work/ResInstall/bin/Res.app/Contents/MacOS/Res" | grep -q "resource 43|level 7"' 'installed .app does not read its resources'
else
    check '"$work/ResInstall/bin/Res" | grep -q "resource 43|level 7"' 'installed program does not read its resources'
fi
if [[ -n "${sim:-}" ]]; then
    beast build "$res" --platform IOSSimulator --config Debug
    check '[[ -f "$res/Binaries/$sim/Debug/Res.app/Assets/greeting.txt" && -f "$res/Binaries/$sim/Debug/Res.app/Assets/Data/level.txt" ]]' 'iOS bundle resources'
    check 'codesign --verify --strict "$res/Binaries/$sim/Debug/Res.app"' 'iOS bundle with resources is not validly signed'
    "${tool[@]}" run "$res" --platform IOSSimulator --no-build >"$work/res-ios.log" 2>&1 || { cat "$work/res-ios.log"; annotate 'iOS resource program failed' "$work/res-ios.log"; exit 1; }
    check 'grep -q "resource 43|level 7|absent" "$work/res-ios.log"' 'iOS: resources not read'
fi
pass "resources ($([[ $os == Mac ]] && echo ".app${sim:+, iOS bundle}" || echo 'next to the program'), dependency target, incremental, install)"

# IDE files: 'new'/'generate' write .vscode on every host and, on macOS, an Xcode project whose
# targets build through Beast (xcodebuild runs here exactly as Product > Build in Xcode).
beast generate "$template"
check 'python3 -c "import json,sys; [json.load(open(f)) for f in sys.argv[1:]]" "$template/.vscode/tasks.json" "$template/.vscode/launch.json" "$template/.vscode/settings.json"' '.vscode files'
check 'grep -q "Template (Debug)" "$template/.vscode/launch.json"' 'VS Code launch configuration'
if [[ $os == Mac ]]; then
    xcodeproj="$template/Template.xcodeproj"
    check 'plutil -lint "$xcodeproj/project.pbxproj" >/dev/null' 'project.pbxproj is not a valid plist'
    xcodebuild -project "$xcodeproj" -list >"$work/xcode-list.log" 2>&1 || { cat "$work/xcode-list.log"; annotate 'xcodebuild -list failed' "$work/xcode-list.log"; exit 1; }
    check 'grep -q "Template" "$work/xcode-list.log"' 'Xcode scheme missing'
    [[ -n "${sim:-}" ]] && check 'grep -q "Template iOS Simulator" "$work/xcode-list.log"' 'iOS Simulator scheme missing'
    rm -rf "$template/Binaries/$platform/Release"
    xcodebuild -project "$xcodeproj" -scheme Template -configuration Release build >"$work/xcode-build.log" 2>&1 ||
        { cat "$work/xcode-build.log"; annotate 'xcodebuild build failed' "$work/xcode-build.log"; exit 1; }
    check '[[ -x "$template/Binaries/$platform/Release/Template.app/Contents/MacOS/Template" ]]' 'xcodebuild did not build Template.app'
    xcodebuild -project "$xcodeproj" -scheme Template -configuration Release clean >"$work/xcode-clean.log" 2>&1 ||
        { cat "$work/xcode-clean.log"; annotate 'xcodebuild clean failed' "$work/xcode-clean.log"; exit 1; }
    pass 'IDE files (.vscode; Xcode project built with xcodebuild)'
else
    pass 'IDE files (.vscode)'
fi

# External projects (Tests/Fixtures/External): AppA and AppB use the SDK project's static library
# target through externalProjects. Both build in parallel; the SDK tree stays clean; an SDK header
# edit rebuilds the SDK and the application; clean --target SDK removes the application's copy.
ext="$work/External"
cp -R "$root/Tests/Fixtures/External" "$ext"
"${tool[@]}" build "$ext/AppA" --config Debug >"$work/ext-a.log" 2>&1 & pa=$!
"${tool[@]}" build "$ext/AppB" --config Debug >"$work/ext-b.log" 2>&1 & pb=$!
wait $pa || { cat "$work/ext-a.log"; annotate 'external project: AppA build failed' "$work/ext-a.log"; exit 1; }
wait $pb || { cat "$work/ext-b.log"; annotate 'external project: AppB build failed' "$work/ext-b.log"; exit 1; }
for app in AppA AppB; do
    "${tool[@]}" run "$ext/$app" --no-build >"$work/ext-run.log" 2>&1 || { cat "$work/ext-run.log"; annotate "$app run failed" "$work/ext-run.log"; exit 1; }
    check 'grep -q "$app 42 1 1 resource" "$work/ext-run.log"' "$app output (external modules, Source/ include path, {ProjectDir}, resources)"
    check '[[ -d "$ext/$app/Intermediate/Build/$platform/Debug/External/SDK/SDK" ]]' "$app: SDK not built under Intermediate/.../External/SDK"
done
check '[[ ! -e "$ext/SDK/Intermediate" && ! -e "$ext/SDK/Binaries" ]]' 'build files were written into the external project'
beast build "$ext/AppA" --config Debug
check '! grep -qE "Done: [1-9][0-9]* executed" "$work/last.log"' 'no-op build of an application with an external project did work'
sed -i.bak 's/#define SDK_VERSION 1/#define SDK_VERSION 2/' "$ext/SDK/Source/Core/Public/SdkCore.h" && rm -f "$ext/SDK/Source/Core/Public/SdkCore.h.bak"
beast build "$ext/AppA" --config Debug
check 'grep -q "Core.cpp" "$work/last.log" && grep -q "Main.cpp" "$work/last.log"' 'SDK header edit did not recompile the SDK and the application'
"${tool[@]}" run "$ext/AppA" --no-build >"$work/ext-run.log" 2>&1
check 'grep -q "AppA 43 2 1 resource" "$work/ext-run.log"' 'application not rebuilt after the SDK header edit'
beast clean "$ext/AppA" --target SDK
check '[[ ! -d "$ext/AppA/Intermediate/Build/$platform/Debug/External/SDK/SDK" ]]' 'clean --target SDK left the external build'
pass 'external projects (parallel consumers, clean external tree, header edit, clean)'

# Android (optional): the same project as an APK, built on this Linux/macOS host.
if [[ "${1:-}" == --android ]]; then
    sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
    ndk="${ANDROID_NDK_HOME:-${ANDROID_NDK_LATEST_HOME:-$sdk/ndk}}"
    [[ -d "$sdk" && -d "$ndk" ]] || { echo "FAIL: --android needs ANDROID_HOME and an NDK"; exit 1; }
    config "c['android']['sdkRoot'] = '$sdk'; c['android']['ndkRoot'] = '$ndk'; c['android']['javaHome'] = '${JAVA_HOME:-}'; c['android']['abis'] = ['arm64-v8a', 'x86_64']; c.setdefault('linux', {})['compiler'] = ''"
    beast build "$project" --target Game --platform Android --config Debug
    apk="$project/Binaries/Android/Debug/Game.apk"
    check '[[ -f "$apk" ]]' 'APK missing'
    for abi in arm64-v8a x86_64; do
        check 'unzip -l "$apk" | grep -q "lib/$abi/libGame.so" && unzip -l "$apk" | grep -q "lib/$abi/libEngine.so"' "APK lacks $abi libraries"
    done
    pass 'Android APK (arm64-v8a + x86_64, C++20 modules, shared dependency target)'
    # External project on Android: the SDK's AndroidAssets and Resources reach the application's APK.
    python3 - "$ext/AppA/Config/BuildConfig.json" "$sdk" "$ndk" "${JAVA_HOME:-}" <<'EOF2'
import json, sys
path, sdk, ndk, java = sys.argv[1:]
c = json.load(open(path))
c["android"] = {"sdkRoot": sdk, "ndkRoot": ndk, "javaHome": java, "abis": ["x86_64"]}
json.dump(c, open(path, "w"), indent=2)
EOF2
    beast build "$ext/AppA" --platform Android --config Debug
    eapk="$ext/AppA/Binaries/Android/Debug/AppA.apk"
    unzip -l "$eapk" >"$work/ext-apk.txt"
    check 'grep -q "assets/SDK/widgets.txt" "$work/ext-apk.txt" && grep -q "assets/Assets/SDK/sdk.txt" "$work/ext-apk.txt"' 'external project files missing from the APK'
    # AndroidJavaSources of the external module: a source root and a single file (placed by its package).
    dex() { unzip -p "$eapk" 'classes*.dex' >"$work/classes.dex"; }
    dex
    check 'grep -aq "Lcom/beast/sdk/BeastWidgets;" "$work/classes.dex" && grep -aq "Lcom/beast/sdk/text/BeastText;" "$work/classes.dex" && grep -aq "text-v1" "$work/classes.dex"' 'Java sources of the external module are not in the APK'
    sed -i.bak 's/text-v1/text-v2/' "$ext/SDK/Source/Widgets/Android/BeastText.java" && rm -f "$ext/SDK/Source/Widgets/Android/BeastText.java.bak"
    beast build "$ext/AppA" --platform Android --config Debug
    dex
    check 'grep -aq "text-v2" "$work/classes.dex"' 'a .java edit did not rebuild the APK'
    sed -i.bak '/BeastText.java/d' "$ext/SDK/Source/Widgets/Widgets.Build.cs" && rm -f "$ext/SDK/Source/Widgets/Widgets.Build.cs.bak"
    beast build "$ext/AppA" --platform Android --config Debug
    dex
    check '! grep -aq "Lcom/beast/sdk/text/BeastText;" "$work/classes.dex" && grep -aq "Lcom/beast/sdk/BeastWidgets;" "$work/classes.dex"' 'a Java file removed from the rules stayed in the APK'
    check '[[ -z "$(find "$ext/AppA/Platforms/Android/app/src" -name "Beast*.java")" ]]' 'Java sources were copied into the Gradle project'
    # ModulesInfo.AndroidNdkRoot + AdditionalSources: the SDK compiles the NDK's native_app_glue with its own definition.
    glue="$(find "$ext/AppA/Intermediate/Build/Android-x86_64/Debug/External/SDK/SDK" -name 'android_native_app_glue*.o' -print -quit)"
    nm="$(find "$ndk" -path '*/bin/llvm-nm' -print -quit)"
    if [[ -n "$glue" ]]; then "$nm" "$glue" >"$work/glue-nm.txt" 2>&1 || true; else { echo "no android_native_app_glue object; objects:"; find "$ext/AppA/Intermediate/Build" -name "*.o"; } >"$work/glue-nm.txt" 2>&1; fi
    cp "$work/glue-nm.txt" "$work/last.log"
    check 'grep -q "T BeastNativeActivityOnCreate" "$work/glue-nm.txt"' 'native_app_glue not compiled from the NDK with its definition'
    pass 'Android APK with an external project (AndroidAssets, Resources, AndroidJavaSources: edit, removal; NDK native_app_glue)'
fi

echo "PASS: $passed groups; skipped: ${skipped[*]:-none}. Projects: $work"
[[ -n "${GITHUB_ACTIONS:-}" ]] && echo "::notice title=Unix.sh::$passed groups passed; skipped: ${skipped[*]:-none}"
