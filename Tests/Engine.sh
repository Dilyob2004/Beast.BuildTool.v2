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
pass 'generated files across targets (precise inputs, clean build, incremental)'

echo "All $passed engine integration checks passed ($work)."
