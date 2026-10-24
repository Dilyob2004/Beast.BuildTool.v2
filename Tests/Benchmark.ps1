param(
    [ValidateSet('Box2D','NativeLibraries','Synthetic')][string]$Dataset = 'Box2D',
    [string]$Label = 'baseline',
    [int]$Units = 256,
    [int]$Jobs = 8,
    [string]$ToolPath
)
$ErrorActionPreference = 'Stop'
if ($Units -lt 1 -or $Jobs -lt 1) { throw 'Units and Jobs must be positive' }
$tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'BuildTool/bin/Beast.BuildTool.exe'
if ($ToolPath) { $tool = [IO.Path]::GetFullPath($ToolPath) }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$root = Join-Path $PSScriptRoot ('Generated/Benchmark-' + $Dataset + '-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'Source/Workload'
New-Item -ItemType Directory -Path $source,(Join-Path $root 'Config') -Force | Out-Null
if ($Dataset -in @('Box2D','NativeLibraries')) {
    $prototype = Join-Path $repo 'Prototypes/Source/ThirdParty/box2d'
    Copy-Item -LiteralPath (Join-Path $prototype 'src'),(Join-Path $prototype 'include') -Destination $source -Recurse
    $header = Join-Path $source 'include/box2d/b2_settings.h'
    if ($Dataset -eq 'NativeLibraries') {
        $imgui = Join-Path $repo 'Prototypes/Source/ThirdParty/ImGui'
        $imguiSource = Join-Path $source 'imgui'
        New-Item -ItemType Directory -Path $imguiSource -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $imgui 'include') -Destination $imguiSource -Recurse
        foreach ($name in @('imgui.cpp','imgui_draw.cpp','imgui_widgets.cpp','imgui_tables.cpp','imgui_demo.cpp')) {
            Copy-Item -LiteralPath (Join-Path $imgui "src/$name") -Destination $imguiSource
        }
    }
} else {
    $header = Join-Path $source 'Shared.h'
    [IO.File]::WriteAllText($header, "#pragma once`n#include <array>`n#include <algorithm>`n#include <numeric>`n#define BENCH_VALUE 7`n")
    for ($i=0; $i -lt $Units; $i++) {
        [IO.File]::WriteAllText((Join-Path $source "Unit$i.cpp"), "#include `"Shared.h`"`nint Function$i(int x) { std::array<int,64> a{}; std::iota(a.begin(), a.end(), x + BENCH_VALUE); std::sort(a.begin(), a.end()); return a[$($i % 64)]; }`n")
    }
}
[IO.File]::WriteAllText((Join-Path $source 'Workload.Build.cs'), @'
using Beast.BuildTool;
public sealed class Workload : ModuleRules {
    public Workload(ModulesInfo info) {
        PublicIncludePaths.Add("include"); PublicIncludePaths.Add("include/box2d");
        PrivateIncludePaths.Add("src"); PrivateIncludePaths.Add(".");
        PrivateIncludePaths.Add("imgui/include");
    }
}
'@)
[IO.File]::WriteAllText((Join-Path $root 'Source/Bench.Target.cs'), @'
using Beast.BuildTool;
public sealed class BenchTarget : TargetRules {
    public BenchTarget(ModulesInfo info) { Type = TargetType.StaticLibrary; Modules.Add("Workload"); }
}
'@)
[IO.File]::WriteAllText((Join-Path $root 'Config/BuildConfig.json'), (@{
    maxParallelJobs=$Jobs; acceleration=@{cacheEnabled=$true; cacheDirectory='Intermediate/Cache'; memoryReserveMB=1024}
} | ConvertTo-Json -Depth 5))
$tracePath = Join-Path $root 'Intermediate/Build/Windows/Release/Bench/BuildTrace.json'
$rows = [Collections.Generic.List[object]]::new()
function Measure-Build([string]$Stage) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & $tool build $root --target Bench --platform Windows --config Release *> (Join-Path $root "$Stage.log")
    $timer.Stop()
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $root/$Stage.log" }
    $trace = Get-Content -LiteralPath $tracePath -Raw | ConvertFrom-Json
    Copy-Item -LiteralPath $tracePath -Destination (Join-Path $root "$Stage.trace.json")
    $row = [pscustomobject]@{
        stage=$Stage; wallMs=$timer.Elapsed.TotalMilliseconds; graphMs=$trace.elapsedMs
        executed=@($trace.actions | Where-Object { -not $_.skipped -and -not $_.cacheHit }).Count
        skipped=@($trace.actions | Where-Object skipped).Count
        cacheHits=@($trace.actions | Where-Object cacheHit).Count
        actionMs=($trace.actions | Measure-Object durationMs -Sum).Sum
    }
    $rows.Add($row); Write-Host ($row | ConvertTo-Json -Compress)
    if ($Stage -like 'noop*' -and $row.executed -ne 0) { throw 'No-op executed build actions' }
}
$original = [IO.File]::ReadAllBytes($header)
$stamp = (Get-Item -LiteralPath $header).LastWriteTimeUtc
try {
    Measure-Build 'cold'
    1..3 | ForEach-Object { Measure-Build "noop$_" }
    # A content edit with the original timestamp must still invalidate compilation.
    [IO.File]::AppendAllText($header, "`n// benchmark header edit`n")
    (Get-Item -LiteralPath $header).LastWriteTimeUtc = $stamp
    Measure-Build 'header'
    # Cold traces may contain only declared inputs; dependency files are authoritative.
    $dependent = @(Get-ChildItem (Join-Path $root 'Intermediate/Build') -Filter '*.deps.json' -Recurse | Where-Object {
        ((Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).Data.Includes) -contains $header
    }).Count
    $headerTrace = Get-Content -LiteralPath $tracePath -Raw | ConvertFrom-Json
    $compiled = @($headerTrace.actions | Where-Object { $_.description -like 'Compiling*' -and -not $_.skipped -and -not $_.cacheHit }).Count
    if ($compiled -ne $dependent -or $dependent -eq 0) { throw "Expected $dependent header consumers, compiled $compiled" }
    Measure-Build 'noop-after-header'
    & $tool clean $root *> (Join-Path $root 'clean.log')
    if ($LASTEXITCODE -ne 0) { throw 'Clean failed' }
    Measure-Build 'cache-restore'
} finally { [IO.File]::WriteAllBytes($header, $original); (Get-Item -LiteralPath $header).LastWriteTimeUtc = $stamp }
$report = [pscustomobject]@{
    label=$Label; dataset=$Dataset; synthetic=($Dataset -eq 'Synthetic'); sourceCount=@(Get-ChildItem $source -Filter '*.cpp' -Recurse).Count
    jobs=$Jobs; logicalProcessors=[Environment]::ProcessorCount; utc=[DateTime]::UtcNow.ToString('o'); toolSha256=(Get-FileHash ([IO.Path]::ChangeExtension($tool, '.dll'))).Hash
    headerConsumers=$dependent; rows=$rows; project=$root
}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'Benchmark.json')
Write-Host "REPORT: $root/Benchmark.json"
