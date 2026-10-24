param(
    [ValidateSet('Synthetic','Box2D')][string]$Dataset = 'Synthetic',
    [ValidateSet('Windows','Android')][string]$Platform = 'Windows',
    [string]$AndroidSdk = $env:ANDROID_HOME,
    [int]$Units = 128,
    [int]$Jobs = 8,
    [int]$BatchSize = 8,
    [int]$Repeats = 1
)
$ErrorActionPreference = 'Stop'
if ($Units -lt 2 -or $Jobs -lt 1 -or $BatchSize -lt 2 -or $BatchSize -gt 64 -or $Repeats -lt 1) { throw 'Invalid benchmark parameters' }
$tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'BuildTool/bin/Beast.BuildTool.exe'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$root = Join-Path $PSScriptRoot ('Generated/Optimizations-' + $Dataset + '-' + $Platform + '-' + [guid]::NewGuid().ToString('N'))
$rows = [Collections.Generic.List[object]]::new()
for ($repeat = 1; $repeat -le $Repeats; ++$repeat) {
    $modes = @('Plain','Pch','Unity','Both'); if ($repeat % 2 -eq 0) { [Array]::Reverse($modes) }
    foreach ($mode in $modes) {
        $project = Join-Path $root "$repeat/$mode"
        $source = Join-Path $project 'Source/Workload'
        New-Item -ItemType Directory -Path $source,(Join-Path $project 'Config') -Force | Out-Null
        if ($Dataset -eq 'Box2D') {
            $prototype = Join-Path $repo 'Prototypes/Source/ThirdParty/box2d'
            Copy-Item -LiteralPath (Join-Path $prototype 'src'),(Join-Path $prototype 'include') -Destination $source -Recurse
            $pchText = "#pragma once`n#include <box2d/box2d.h>`n"
            $edit = Join-Path $source 'src/b2_body.cpp'
        } else {
            $pchText = "#pragma once`n#include <array>`n#include <algorithm>`n#include <numeric>`n#include <vector>`n"
            for ($i=0; $i -lt $Units; ++$i) {
                [IO.File]::WriteAllText((Join-Path $source ("Unit{0:D4}.cpp" -f $i)), "#include `"Pch.h`"`nint Function$i(int x) { std::array<int,64> a{}; std::iota(a.begin(), a.end(), x + 7); std::sort(a.begin(), a.end()); return a[$($i % 64)]; }`n")
            }
            $edit = Join-Path $source 'Unit0000.cpp'
        }
        [IO.File]::WriteAllText((Join-Path $source 'Pch.h'), $pchText)
        $rules = @'
using Beast.BuildTool;
public sealed class Workload : ModuleRules {
    public Workload(ModulesInfo info) {
        PublicIncludePaths.Add("include"); PublicIncludePaths.Add("include/box2d");
        PrivateIncludePaths.Add("src"); PrivateIncludePaths.Add(".");
        PrecompiledHeaderSource = PCH_HEADER;
        UseUnityBuild = UNITY_ENABLED; UnityBatchSize = BATCH_SIZE;
    }
}
'@
        $rules = $rules.Replace('PCH_HEADER', $(if ($mode -in @('Pch','Both')) {'"Pch.h"'} else {'""'})).Replace('UNITY_ENABLED', ($mode -in @('Unity','Both')).ToString().ToLowerInvariant()).Replace('BATCH_SIZE', "$BatchSize")
        [IO.File]::WriteAllText((Join-Path $source 'Workload.Build.cs'), $rules)
        [IO.File]::WriteAllText((Join-Path $project 'Source/Bench.Target.cs'), @'
using Beast.BuildTool;
public sealed class BenchTarget : TargetRules {
    public BenchTarget(ModulesInfo info) { Type = TargetType.StaticLibrary; Modules.Add("Workload"); }
}
'@)
        $settings = @{ maxParallelJobs=$Jobs; acceleration=@{cacheEnabled=$false; memoryReserveMB=1024} }
        if ($AndroidSdk) { $settings.android = @{sdkRoot=$AndroidSdk; ndkRoot=(Join-Path $AndroidSdk 'ndk')} }
        [IO.File]::WriteAllText((Join-Path $project 'Config/BuildConfig.json'), ($settings | ConvertTo-Json -Depth 5))
        $tracePath = Join-Path $project "Intermediate/Build/$Platform/Release/Bench/BuildTrace.json"
        foreach ($stage in @('cold','noop1','noop2','noop3','source-edit','noop-after-edit')) {
            if ($stage -eq 'source-edit') {
                $stamp = (Get-Item $edit).LastWriteTimeUtc
                [IO.File]::AppendAllText($edit, "`n// Same-timestamp source edit for incremental benchmark.`n")
                (Get-Item $edit).LastWriteTimeUtc = $stamp
            }
            $timer = [Diagnostics.Stopwatch]::StartNew()
            & $tool build $project --target Bench --platform $Platform --config Release *> (Join-Path $project "$stage.log")
            $timer.Stop()
            if ($LASTEXITCODE -ne 0) { throw "Benchmark failed: $project/$stage.log" }
            $trace = Get-Content $tracePath -Raw | ConvertFrom-Json
            Copy-Item $tracePath (Join-Path $project "$stage.trace.json")
            $executed = @($trace.actions | Where-Object { -not $_.skipped -and -not $_.cacheHit })
            if ($stage -like 'noop*' -and $executed.Count -ne 0) { throw "$mode no-op rebuilt" }
            if ($stage -eq 'source-edit' -and (@($executed | Where-Object { $_.description -like 'Compiling*' }).Count -ne 1 -or @($executed | Where-Object { $_.description -like 'Creating PCH*' }).Count -ne 0)) { throw 'Source edit was not confined to one unit/batch' }
            $row = [pscustomobject]@{ mode=$mode; repeat=$repeat; stage=$stage; wallMs=$timer.Elapsed.TotalMilliseconds; graphMs=$trace.elapsedMs; executed=$executed.Count; skipped=@($trace.actions | Where-Object skipped).Count; peakProcessMB=($trace.actions | Measure-Object peakMemoryMB -Maximum).Maximum }
            $rows.Add($row); Write-Host ($row | ConvertTo-Json -Compress)
        }
    }
}
$report = [pscustomobject]@{ dataset=$Dataset; platform=$Platform; synthetic=($Dataset -eq 'Synthetic'); units=$(if ($Dataset -eq 'Box2D') {45} else {$Units}); jobs=$Jobs; batchSize=$BatchSize; repeats=$Repeats; utc=[DateTime]::UtcNow.ToString('o'); toolSha256=(Get-FileHash ([IO.Path]::ChangeExtension($tool,'.dll'))).Hash; rows=$rows }
$report | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $root 'Comparison.json')
Write-Host "REPORT: $root/Comparison.json"
