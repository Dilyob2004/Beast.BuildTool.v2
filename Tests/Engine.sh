#!/usr/bin/env bash
# Integration tests of the features an engine with editor and game projects needs (BeastEngine layout):
# rules of the first Beast BuildTool (ThirdPartyDependices), Source/ThirdParty, include roots,
# build options, shared rules helpers (*.Rules.cs), one build graph across targets, external projects
# built in their own tree, missing-header hints, build steps that run a tool target, workspaces.
#   Tests/Engine.sh            all checks (Linux, macOS)
#   BEAST=/path/to/tool        tool to test (default: BuildTool/bin/Beast.BuildTool, or the .dll via dotnet)
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
work="$here/Generated/Engine-$(date +%s)-$$"
passed=0
annotate() {
    [[ -n "${GITHUB_ACTIONS:-}" ]] || return 0
    local text="$1"
    [[ -n "${2:-}" && -f "$2" ]] && text+=$'\n'"$(grep -v ' Verbose ' "$2" | tail -n 40)"
    text="${text//'%'/'%25'}"; text="${text//$'\r'/}"; text="${text//$'\n'/'%0A'}"
    echo "::error title=Engine.sh::$text"
}
beast() { "${tool[@]}" "$@" >"$work/last.log" 2>&1 || { cat "$work/last.log"; echo "FAIL: beast $*"; annotate "beast $* failed" "$work/last.log"; exit 1; }; }
# A build that must fail; its log stays in last.log.
beast_fails() { if "${tool[@]}" "$@" >"$work/last.log" 2>&1; then cat "$work/last.log"; echo "FAIL: beast $* succeeded"; annotate "beast $* should fail" "$work/last.log"; exit 1; fi; }
check() { if ! eval "$1"; then echo "FAIL: $2"; annotate "$2" "$work/last.log"; exit 1; fi; }
pass() { echo "PASS: $1"; passed=$((passed + 1)); }
put() { mkdir -p "$(dirname "$project/$1")"; printf '%s\n' "$2" >"$project/$1"; }
config() { python3 - "$project/Config/BuildConfig.json" "$1" <<'EOF'
import json, os, sys
path, change = sys.argv[1], sys.argv[2]
c = json.load(open(path)) if os.path.exists(path) else {}
exec(change)
os.makedirs(os.path.dirname(path), exist_ok=True)
json.dump(c, open(path, "w"), indent=2)
EOF
}
bin() { echo "$project/Binaries/$platform/$1"; }
mkdir -p "$work"
# Names differing only in case (HTTPS_PROXY / https_proxy on CI and proxied hosts): the key of a build
# step includes the environment, whose order must not change between runs.
export BEAST_CASE_A=1 beast_case_a=2 BEAST_CASE_B=3 beast_case_b=4 BEAST_CASE_C=5 beast_case_c=6

# ---------------------------------------------------------------------------------------------
# Engine: rules of the first Beast BuildTool. Modules live under Source/Runtime/<Module> and include
# each other as "Core/Public/Core.h" (include root Source/Runtime); vendored libraries are modules
# under Source/ThirdParty, used through ThirdPartyDependices and located with info.ThirdPartyDirectory;
# the RHI is chosen by a build option; a *.Rules.cs helper is shared by several modules.
# ---------------------------------------------------------------------------------------------
project="$work/Engine"
put Source/Rules/EnginePaths.Rules.cs 'using System.IO;
using Beast.BuildTool;
public static class EnginePaths
{
    public static string Vendor(ModulesInfo info, string name) => Path.Combine(info.ThirdPartyDirectory, name);
    public const string RhiOption = "RHI";
}'
put Source/ThirdParty/mathlib/mathlib.Build.cs 'using Beast.BuildTool;
public class mathlib : ModuleRules
{
    public mathlib(ModulesInfo Info)
    {
        Type = ModuleType.HeaderOnly;
        PublicIncludePaths.Add(System.IO.Path.Combine(EnginePaths.Vendor(Info, "mathlib"), "include"));
    }
}'
put Source/ThirdParty/mathlib/include/mathlib/mathlib.h '#pragma once
inline int MathTwice(int value) { return value * 2; }'
put Source/Runtime/Core/Core.Build.cs 'using Beast.BuildTool;
public class Core : ModuleRules
{
    public Core(ModulesInfo Info)
    {
        PublicIncludePaths.Add("Public");
        ThirdPartyDependices.Add("mathlib");
        PublicDefinitions.Add("ENGINE_RHI_" + Info.GetOption(EnginePaths.RhiOption, "Vulkan").ToUpperInvariant() + "=1");
        if (Info.GetBoolOption("Tracing")) Definitions.Add("ENGINE_TRACING=1");
    }
}'
put Source/Runtime/Core/Public/Core.h '#pragma once
CORE_API int CoreValue();
CORE_API const char* CoreRhi();
CORE_API int CoreTracing();'
put Source/Runtime/Core/Private/Core.cpp '#include "Core/Public/Core.h"
#include "mathlib/mathlib.h"
int CoreValue() { return MathTwice(21); }
const char* CoreRhi()
{
#if defined(ENGINE_RHI_VULKAN)
    return "vulkan";
#elif defined(ENGINE_RHI_DX12)
    return "dx12";
#else
    return "unknown";
#endif
}
int CoreTracing()
{
#if defined(ENGINE_TRACING)
    return 1;
#else
    return 0;
#endif
}'
put Source/Runtime/Launch/Launch.Build.cs 'using Beast.BuildTool;
public class Launch : ModuleRules
{
    public Launch(ModulesInfo Info) { PrivateDependencies.Add("Core"); ThirdPartyDependices.Add("mathlib"); }
}'
put Source/Runtime/Launch/Private/Main.cpp '#include "Core/Public/Core.h"
#include "mathlib/mathlib.h"
#include <cstdio>
int main() { std::printf("engine %d %s %d %d\n", CoreValue(), CoreRhi(), CoreTracing(), MathTwice(5)); return 0; }'
put Source/BeastEngine.Target.cs 'using Beast.BuildTool;
public sealed class BeastEngineTarget : TargetRules
{
    public BeastEngineTarget(ModulesInfo info) { Type = TargetType.SharedLibrary; Modules.Add("Core"); }
}'
put Source/Engine.Target.cs 'using Beast.BuildTool;
public sealed class EngineTarget : TargetRules
{
    public EngineTarget(ModulesInfo info) { Modules.Add("Launch"); TargetDependencies.Add("BeastEngine"); }
}'
config 'c["includeRoots"] = ["Source/Runtime"]; c["options"] = {"RHI": "Vulkan"}'
run_engine() { "$(bin Debug/Engine)" >"$work/engine.log" 2>&1 || { cat "$work/engine.log"; echo "FAIL: engine run"; exit 1; }; }

beast build "$project" --target Engine --config Debug
check 'grep -q "ThirdPartyDependices is deprecated" "$work/last.log"' 'ThirdPartyDependices warning'
run_engine
check 'grep -q "^engine 42 vulkan 0 10$" "$work/engine.log"' 'ThirdPartyDependices as PublicDependencies, Source/ThirdParty, include root, option default'
pass 'rules of the first Beast BuildTool (ThirdPartyDependices, Source/ThirdParty, includeRoots, *.Rules.cs)'

# --option overrides the configured value; a changed option rebuilds what it changes, the same value
# again is a no-op; boolean options; bad names and values are errors.
beast build "$project" --target Engine --config Debug --option RHI=DX12 --option Tracing=on
run_engine
check 'grep -q "^engine 42 dx12 1 10$" "$work/engine.log"' '--option override (RHI=DX12, Tracing=on)'
beast build "$project" --target Engine --config Debug --option RHI=DX12 --option Tracing=on
check 'grep -q "Done:" "$work/last.log" && ! grep "Done:" "$work/last.log" | grep -vq "Done: 0 executed (0 remote), 0 cache hits"' 'same options rebuilt'
beast_fails build "$project" --target Engine --config Debug --option Tracing=maybe
check 'grep -q "must be a boolean" "$work/last.log"' 'bad boolean option accepted'
beast_fails build "$project" --target Engine --config Debug --option 9bad=1
check 'grep -q "invalid option name" "$work/last.log"' 'bad option name accepted'
beast build "$project" --target Engine --config Debug
run_engine
check 'grep -q "^engine 42 vulkan 0 10$" "$work/engine.log"' 'configured option after an override'
pass 'build options (config, --option, bool, validation, rebuild)'

# migrate rewrites ThirdPartyDependices (BOM kept); the warning disappears and the result is the same.
printf '\xEF\xBB\xBF' | cat - "$project/Source/Runtime/Core/Core.Build.cs" >"$work/bom" && mv "$work/bom" "$project/Source/Runtime/Core/Core.Build.cs"
beast migrate "$project"
check '! grep -rq ThirdPartyDependices "$project/Source" && grep -q "PublicDependencies.Add(\"mathlib\")" "$project/Source/Runtime/Launch/Launch.Build.cs"' 'migrate did not rewrite ThirdPartyDependices'
check '[[ "$(head -c 3 "$project/Source/Runtime/Core/Core.Build.cs" | od -An -tx1 | tr -d " ")" == efbbbf ]]' 'migrate dropped the BOM'
beast build "$project" --target Engine --config Debug
check '! grep -q "ThirdPartyDependices is deprecated" "$work/last.log"' 'warning after migrate'
run_engine
check 'grep -q "^engine 42 vulkan 0 10$" "$work/engine.log"' 'build after migrate'
# thirdPartyDirectory: an explicit directory; a missing one and a missing include root are errors.
mkdir -p "$project/Vendor/mathlib/include/mathlib"
printf '#pragma once\ninline int MathTwice(int value) { return value * 2 + 1; }\n' >"$project/Vendor/mathlib/include/mathlib/mathlib.h"
config 'c["thirdPartyDirectory"] = "Vendor"'
beast build "$project" --target Engine --config Debug
run_engine
check 'grep -q "^engine 43 vulkan 0 11$" "$work/engine.log"' 'thirdPartyDirectory'
config 'c["thirdPartyDirectory"] = "Missing"'
beast_fails build "$project" --target Engine --config Debug
check 'grep -q "thirdPartyDirectory .* does not exist" "$work/last.log"' 'missing thirdPartyDirectory accepted'
config 'del c["thirdPartyDirectory"]; c["includeRoots"] = ["Source/Runtime", "Source/Nope"]'
beast_fails build "$project" --target Engine --config Debug
check 'grep -q "include root .* does not exist" "$work/last.log"' 'missing include root accepted'
config 'c["includeRoots"] = ["Source/Runtime"]'
pass 'migrate (ThirdPartyDependices), thirdPartyDirectory, validation'

# ---------------------------------------------------------------------------------------------
# One build graph for the target and its dependency targets: the application's compilation does not
# wait for the engine library's link, its link does; per-target traces keep their own actions;
# 'beast graph' prints the same plan (JSON on stdout, DOT) without building.
# ---------------------------------------------------------------------------------------------
graph_json="$work/graph.json"
"${tool[@]}" graph "$project" --target Engine --config Debug >"$graph_json" 2>"$work/last.log" || { cat "$work/last.log"; echo "FAIL: beast graph"; exit 1; }
python3 - "$graph_json" "$so" <<'EOF2'
import json, sys
g = json.load(open(sys.argv[1])); so = sys.argv[2]
by = {a["id"]: a for a in g["actions"]}
def find(prefix):
    hits = [a for a in g["actions"] if a["description"].startswith(prefix)]
    assert len(hits) == 1, (prefix, [a["description"] for a in g["actions"]])
    return hits[0]
def closure(a):
    seen, stack = set(), list(a["dependencies"])
    while stack:
        i = stack.pop()
        if i in seen: continue
        seen.add(i); stack.extend(by[i]["dependencies"])
    return seen
engine_link = find("Linking libBeastEngine." + so)
main = find("Compiling Main.cpp")
app_link = find("Linking Engine")
assert engine_link["id"] in app_link["dependencies"], "application link does not wait for the engine library"
assert engine_link["id"] not in closure(main), "application compilation waits for the engine link"
assert {a["target"] for a in g["actions"]} >= {t for t in g["targets"]} and len(g["targets"]) == 2, g["targets"]
assert main["target"].endswith("/Engine") and engine_link["target"].endswith("/BeastEngine")
assert g["depth"] >= 3
EOF2
check '[[ $? -eq 0 ]]' 'graph structure'
"${tool[@]}" graph "$project" --target Engine --config Debug --format dot --output "$work/graph.dot" >"$work/last.log" 2>&1 || { cat "$work/last.log"; exit 1; }
check 'head -1 "$work/graph.dot" | grep -q "^digraph BeastBuild" && grep -q "cluster_1" "$work/graph.dot" && grep -q " -> " "$work/graph.dot"' 'DOT graph'
beast_fails graph "$project" --target Engine --format svg
check 'grep -q "Unknown graph format" "$work/last.log"' 'unknown graph format accepted'
rm -rf "$project/Intermediate" "$project/Binaries"
beast build "$project" --target Engine --config Debug
check '[[ $(grep -c "Done:" "$work/last.log") -eq 1 ]]' 'build ran more than one graph'
traces="$project/Intermediate/Build/$platform/Debug"
python3 - "$traces" "$so" <<'EOF2'
import json, sys, os
root, so = sys.argv[1], sys.argv[2]
whole = json.load(open(os.path.join(root, "Engine", "BuildGraph.json")))["actions"]
engine = json.load(open(os.path.join(root, "BeastEngine", "BuildTrace.json")))["actions"]
app = json.load(open(os.path.join(root, "Engine", "BuildTrace.json")))["actions"]
names = lambda actions: {a["description"] for a in actions}
assert names(engine) | names(app) == names(whole), (names(engine), names(app), names(whole))
assert not (names(engine) & names(app))
assert any(d.startswith("Linking libBeastEngine") for d in names(engine)) and any(d.startswith("Linking Engine") for d in names(app))
assert all(a["status"] == "Completed" for a in whole)
EOF2
check '[[ $? -eq 0 ]]' 'BuildGraph.json / per-target BuildTrace.json'
run_engine
check 'grep -q "^engine 42 vulkan 0 10$" "$work/engine.log"' 'clean build in one graph'
pass 'one build graph across targets (edges, traces, beast graph json/dot)'

# A generated header of the engine library (BuildStep of Core) read by the application, another
# target: the application's compilation depends on the generator itself; a changed generator input
# recompiles the application's source, not unrelated actions.
put Source/Runtime/Core/Tools/version.py 'import sys, pathlib
out = pathlib.Path(sys.argv[2]); out.parent.mkdir(parents=True, exist_ok=True)
out.write_text("#pragma once\n#define ENGINE_VERSION " + pathlib.Path(sys.argv[1]).read_text().strip() + "\n")'
put Source/Runtime/Core/Version.txt '7'
python3 - "$project/Source/Runtime/Core/Core.Build.cs" <<'EOF2'
import sys
p = sys.argv[1]; s = open(p, encoding="utf-8-sig").read()
s = s.replace('PublicIncludePaths.Add("Public");', 'PublicIncludePaths.Add("Public"); PublicIncludePaths.Add("{GeneratedDir}"); var step = new BuildStep { Name = "Version", Executable = "python3" }; step.Arguments.AddRange(new[] { "{ModuleDir}/Tools/version.py", "{ModuleDir}/Version.txt", "{GeneratedDir}/EngineVersion.h" }); step.Inputs.AddRange(new[] { "Tools/version.py", "Version.txt" }); step.Outputs.Add("EngineVersion.h"); BuildSteps.Add(step);')
open(p, "w", encoding="utf-8").write(s)
EOF2
put Source/Runtime/Launch/Private/Main.cpp '#include "Core/Public/Core.h"
#include "mathlib/mathlib.h"
#include "EngineVersion.h"
#include <cstdio>
int main() { std::printf("engine %d %s %d %d v%d\n", CoreValue(), CoreRhi(), CoreTracing(), MathTwice(5), ENGINE_VERSION); return 0; }'
"${tool[@]}" graph "$project" --target Engine --config Debug >"$graph_json" 2>"$work/last.log" || { cat "$work/last.log"; exit 1; }
python3 - "$graph_json" <<'EOF2'
import json, sys
g = json.load(open(sys.argv[1]))
gen = [a for a in g["actions"] if a["description"].startswith("Generating Core/Version")]
main = [a for a in g["actions"] if a["description"].startswith("Compiling Main.cpp")]
assert len(gen) == 1 and len(main) == 1
assert gen[0]["id"] in main[0]["dependencies"], "application compile does not depend on the engine generator"
assert any(i.endswith("EngineVersion.h") for i in main[0]["inputs"]), "generated header is not an input of the consumer"
assert gen[0]["target"].endswith("/BeastEngine")
EOF2
check '[[ $? -eq 0 ]]' 'cross-target generated header edge'
rm -rf "$project/Intermediate" "$project/Binaries"
beast build "$project" --target Engine --config Debug
run_engine
check 'grep -q "^engine 42 vulkan 0 10 v7$" "$work/engine.log"' 'clean build with a cross-target generated header'
put Source/Runtime/Core/Version.txt '8'
beast build "$project" --target Engine --config Debug
run_engine
check 'grep -q " v8$" "$work/engine.log"' 'generator input change not propagated to the application'
check 'grep -q "Compiling Main.cpp" "$work/last.log"' 'consumer not recompiled'
for _ in 1 2 3; do
    beast build "$project" --target Engine --config Debug
    check 'grep -q "Done: 0 executed (0 remote), 0 cache hits" "$work/last.log"' 'build step reran without a change (environment order)'
done
pass 'generated files across targets (precise inputs, clean build, incremental)'

# A module that includes headers of modules it does not depend on (rules ported from a tool that
# merged all settings): the failed build names the providing module and the dependency to add.
put Source/Runtime/Renderer/Renderer.Build.cs 'using Beast.BuildTool;
public class Renderer : ModuleRules { public Renderer(ModulesInfo Info) { PublicIncludePaths.Add("Public"); } }'
put Source/Runtime/Renderer/Private/Renderer.cpp '#include "Core/Public/Core.h"
#include "mathlib/mathlib.h"
int RenderValue() { return CoreValue() + MathTwice(1); }'
cp "$project/Source/BeastEngine.Target.cs" "$work/BeastEngine.Target.cs"
sed -i.bak 's/Modules.Add("Core");/Modules.Add("Core"); Modules.Add("Renderer");/' "$project/Source/BeastEngine.Target.cs" && rm -f "$project/Source/BeastEngine.Target.cs.bak"
beast_fails build "$project" --target BeastEngine --config Debug
# Core/Public/Core.h resolves through the include root (visible to every module, like Source/);
# mathlib's header only through a dependency.
check 'grep -q "'"'"'mathlib/mathlib.h'"'"' is provided by module '"'"'mathlib'"'"'.*PrivateDependencies.Add(\"mathlib\").*Renderer.Build.cs" "$work/last.log"' 'no hint for a header of another module'
check '[[ $(grep -c "is provided by module" "$work/last.log") -eq 1 ]]' 'hint repeated or for a visible header'
sed -i.bak 's/PublicIncludePaths.Add("Public"); }/PublicIncludePaths.Add("Public"); PrivateDependencies.Add("Core"); }/' "$project/Source/Runtime/Renderer/Renderer.Build.cs" && rm -f "$project/Source/Runtime/Renderer/Renderer.Build.cs.bak"
# Core exposes mathlib publicly (ThirdPartyDependices = PublicDependencies): through Core, Renderer sees it too.
beast build "$project" --target BeastEngine --config Debug
put Source/Runtime/Renderer/Private/Renderer.cpp '#include "Launch/Private/Missing.h"
int RenderValue() { return 0; }'
beast_fails build "$project" --target BeastEngine --config Debug
check '! grep -q "is provided by module" "$work/last.log"' 'hint for a header that does not exist'
rm -rf "$project/Source/Runtime/Renderer"; cp "$work/BeastEngine.Target.cs" "$project/Source/BeastEngine.Target.cs"
beast build "$project" --target Engine --config Debug
pass 'missing-header hints (providing module, dependency to declare)'

# ---------------------------------------------------------------------------------------------
# A game project that uses the engine with "build": "shared": the engine's targets build in the
# engine's own Intermediate/Binaries (the tree the engine and its editor use), so a game build after
# an engine build does no engine work and links the same library; the library is copied beside the
# game's products; the engine's own build after a game build is a no-op (same command lines).
# ---------------------------------------------------------------------------------------------
engine="$project"
game="$work/Projects/TestGame"
project="$game"
config 'c["externalProjects"] = [{"name": "Beast", "path": "../../Engine", "build": "shared"}]'
put Source/TestGame/TestGame.Build.cs 'using Beast.BuildTool;
public class TestGame : ModuleRules
{
    public TestGame(ModulesInfo Info) { PublicIncludePaths.Add("Public"); PrivateDependencies.Add("Core"); }
}'
put Source/TestGame/Public/TestGame.h '#pragma once
TESTGAME_API int GameScore();'
put Source/TestGame/Private/TestGame.cpp '#include "TestGame.h"
#include "Core/Public/Core.h"
int GameScore() { return CoreValue() + 1; }'
put Source/GameMain/GameMain.Build.cs 'using Beast.BuildTool;
public class GameMain : ModuleRules
{
    public GameMain(ModulesInfo Info) { PrivateDependencies.Add("TestGame"); PrivateDependencies.Add("Core"); }
}'
put Source/GameMain/Main.cpp '#include "TestGame.h"
#include "Core/Public/Core.h"
#include <cstdio>
int main() { std::printf("game %d %s\n", GameScore(), CoreRhi()); return 0; }'
put Source/TestGame.Target.cs 'using Beast.BuildTool;
public sealed class TestGameTarget : TargetRules
{
    public TestGameTarget(ModulesInfo info) { Type = TargetType.SharedLibrary; Modules.Add("TestGame"); TargetDependencies.Add("BeastEngine"); }
}'
put Source/GameApp.Target.cs 'using Beast.BuildTool;
public sealed class GameAppTarget : TargetRules
{
    public GameAppTarget(ModulesInfo info) { Modules.Add("GameMain"); TargetDependencies.Add("TestGame"); TargetDependencies.Add("BeastEngine"); }
}'
engine_lib="$engine/Binaries/$platform/Debug/libBeastEngine.$so"
project="$engine"; beast build "$engine" --target Engine --config Debug; project="$game"
stamp() { stat -c %Y.%s "$1" 2>/dev/null || stat -f %m.%z "$1"; }
before="$(stamp "$engine_lib")"
sleep 1
beast build "$game" --target GameApp --config Debug
check '[[ "$(stamp "$engine_lib")" == "$before" ]]' 'game build relinked the shared engine library'
check '! grep -q "Linking libBeastEngine\|Compiling Core.cpp" "$work/last.log"' 'game build did engine work'
check '[[ ! -d "$game/Intermediate/Build/$platform/Debug/External" && -f "$engine/Intermediate/Build/$platform/Debug/BeastEngine/BuildTrace.json" ]]' 'shared engine target built inside the game'
check '[[ -f "$game/Binaries/$platform/Debug/libBeastEngine.$so" && ! -f "$game/Binaries/$platform/Debug/libmathlib.$so" ]]' 'engine library not copied beside the game'
"$game/Binaries/$platform/Debug/GameApp" >"$work/game.log" 2>&1 || { cat "$work/game.log"; echo "FAIL: game run"; exit 1; }
check 'grep -q "^game 43 vulkan$" "$work/game.log"' 'game with the shared engine'
# An engine source change: the game build rebuilds the engine in the engine tree; the engine's own
# build afterwards finds everything up to date.
sed -i.bak 's/return MathTwice(21);/return MathTwice(21) + 100;/' "$engine/Source/Runtime/Core/Private/Core.cpp" && rm -f "$engine/Source/Runtime/Core/Private/Core.cpp.bak"
beast build "$game" --target GameApp --config Debug
check 'grep -q "Compiling Core.cpp" "$work/last.log" && grep -q "Linking libBeastEngine" "$work/last.log"' 'engine change not rebuilt by the game build'
"$game/Binaries/$platform/Debug/GameApp" >"$work/game.log" 2>&1
check 'grep -q "^game 143 vulkan$" "$work/game.log"' 'game did not pick up the rebuilt engine library'
project="$engine"; beast build "$engine" --target BeastEngine --config Debug; project="$game"
check 'grep -q "Done: 0 executed (0 remote), 0 cache hits" "$work/last.log"' 'engine build after a game build was not a no-op (different command lines)'
"${tool[@]}" graph "$game" --target GameApp --config Debug >"$graph_json" 2>"$work/last.log"
check 'python3 -c "import json,sys; g=json.load(open(sys.argv[1])); assert any(t.endswith(\"/BeastEngine\") for t in g[\"targets\"])" "$graph_json"' 'graph of the game lacks the engine target'
config 'c["externalProjects"][0]["build"] = "copy"'
beast_fails build "$game" --target GameApp --config Debug
check 'grep -q "must be \"local\" or \"shared\"" "$work/last.log"' 'bad build mode accepted'
config 'c["externalProjects"][0]["build"] = "shared"'
pass 'external project built in its own tree (build: shared)'

echo "All $passed engine integration checks passed ($work)."
