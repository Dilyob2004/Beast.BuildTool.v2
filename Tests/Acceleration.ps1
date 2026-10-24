param([string]$AndroidSdk)
$ErrorActionPreference = 'Stop'
$tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'BuildTool/bin/Beast.BuildTool.exe'
$testRoot = Join-Path $PSScriptRoot ('Generated/Acceleration ' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $testRoot 'AccelDemo'
function Beast([string[]]$Arguments) {
    & $tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Beast failed: $Arguments" }
}
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Remove-TestDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    Check ($resolved.StartsWith([IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar)) 'Unsafe test deletion'
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
Beast @('new', 'AccelDemo', '--path', $testRoot)
$configPath = Join-Path $project 'Config/BuildConfig.json'
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$config.acceleration.SharedCacheDirectory = Join-Path $testRoot 'SharedCache'
function Save-Config { [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10)) }
Save-Config
$launch = Join-Path $project 'Source/Launch/Launch.cpp'
$header = Join-Path $project 'Source/Launch/Value.h'
[IO.File]::WriteAllText($header, "#pragma once`n#define TEST_VALUE 41`n")
$text = [IO.File]::ReadAllText($launch).Replace('#include <iostream>', "#include <iostream>`n#include `"Value.h`"").Replace('"Hello from AccelDemo!" <<', 'TEST_VALUE <<')
$text = $text.Replace('#include "Value.h"', "#include `"Value.h`"`n#if __has_include(`"Optional.h`")`n#undef TEST_VALUE`n#define TEST_VALUE 43`n#endif")
[IO.File]::WriteAllText($launch, $text)
$exe = Join-Path $project 'Binaries/Windows/Debug/AccelDemo.exe'
$tracePath = Join-Path $project 'Intermediate/Build/Windows/Debug/AccelDemo/BuildTrace.json'
function Trace { return (Get-Content $tracePath -Raw | ConvertFrom-Json).actions }
function Build { Beast @('build', $project, '--config', 'Debug') }
Build
Check ((& $exe) -eq '41') 'Initial app failed'
$stamp = (Get-Item $header).LastWriteTimeUtc
(Get-Item $header).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(1)
Build
Check (@(Trace | Where-Object { -not $_.skipped }).Count -eq 0) 'Touch-only change rebuilt'
[IO.File]::WriteAllText($header, "#pragma once`n#define TEST_VALUE 42`n")
(Get-Item $header).LastWriteTimeUtc = $stamp
Build
Check ((& $exe) -eq '42') 'Same-timestamp content edit missed'
[IO.File]::WriteAllText($header, "#pragma once`n#define TEST_VALUE 41`n")
(Get-Item $header).LastWriteTimeUtc = $stamp
Build
Check (@(Trace | Where-Object cacheHit).Count -ge 1) 'Reverted header was not restored from cache'
Check ((& $exe) -eq '41') 'Restored app failed'
Beast @('clean', $project)
Build
Check (@(Trace | Where-Object cacheHit).Count -ge 1) 'Clean lost compile cache'
$obj = Get-ChildItem (Join-Path $project 'Intermediate/Build/Windows/Debug/AccelDemo/Objects') -Filter '*.obj' -Recurse | Select-Object -First 1
[IO.File]::WriteAllText($obj.FullName, 'corrupt')
Build
Check (@(Trace | Where-Object cacheHit).Count -ge 1) 'Corrupt output not restored'
Remove-TestDirectory (Join-Path $project 'Intermediate/Cache')
Beast @('clean', $project)
Build
Check (@(Trace | Where-Object cacheHit).Count -ge 1) 'Shared cache miss after local cache removal'
$optional = Join-Path $project 'Source/Launch/Optional.h'
[IO.File]::WriteAllText($optional, '// optional feature')
Build
Check ((& $exe) -eq '43') 'New optional header was not detected'
Remove-Item -LiteralPath $optional
Build
Check ((& $exe) -eq '41') 'Removed optional header was not detected'
$config.acceleration.MaxMemoryMB = 1
[IO.File]::AppendAllText($launch, "`n// Require a new compile for the memory admission negative test.`n")
Save-Config
$log = (& $tool build $project --config Debug 2>&1 | Out-String)
Check ($LASTEXITCODE -ne 0 -and $log.Contains('Not enough memory')) 'Memory limit not enforced'
$config.acceleration.MaxMemoryMB = 0
$config.acceleration.CacheEnabled = $false
$config.acceleration.WorkerQueueDirectory = Join-Path $testRoot 'Queue'
$config.acceleration.WorkerTimeoutSeconds = 30
$config.acceleration.WorkerKeyEnvironment = 'BEAST_ACCELERATION_TEST_KEY'
Save-Config
$keyBackup = $env:BEAST_ACCELERATION_TEST_KEY
$env:BEAST_ACCELERATION_TEST_KEY = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
function Start-Worker([string]$CompilerRoot) {
    $info = [Diagnostics.ProcessStartInfo]::new($tool)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in @('worker','--queue',$config.acceleration.WorkerQueueDirectory,'--project-root',$project,'--compiler-root',$CompilerRoot,'--key-env','BEAST_ACCELERATION_TEST_KEY','--once')) { $info.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($info)
}
$androidWorker = $null
try {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $mixedQueue = $config.acceleration.WorkerQueueDirectory
    if ($AndroidSdk) { $androidWorker = Start-Worker (Join-Path $AndroidSdk 'ndk') }
    $worker = Start-Worker (Join-Path $vsRoot 'VC/Tools/MSVC')
    try {
        Beast @('clean', $project)
        Build
        Check (@(Trace | Where-Object remote).Count -eq 1) 'Windows worker did not compile'
        Check ((& $exe) -eq '41') 'Worker-produced Windows app failed'
        Check ($worker.WaitForExit(10000)) 'Worker did not finish'
        if ($androidWorker) { Check (-not $androidWorker.HasExited) 'NDK worker claimed incompatible MSVC job' }
    } finally { if (-not $worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() }; $worker.Dispose() }
    # Kill our worker after it claims the request, then let a fresh worker serve the new attempt.
    $config.acceleration.WorkerQueueDirectory = Join-Path $testRoot 'RecoveryQueue'
    $config.acceleration.WorkerTimeoutSeconds = 8
    $config.acceleration.WorkerRetryCount = 1
    Save-Config
    Beast @('clean', $project)
    $worker = Start-Worker (Join-Path $vsRoot 'VC/Tools/MSVC')
    $buildInfo = [Diagnostics.ProcessStartInfo]::new($tool)
    $buildInfo.UseShellExecute = $false; $buildInfo.CreateNoWindow = $true
    $buildInfo.RedirectStandardOutput = $true; $buildInfo.RedirectStandardError = $true
    foreach ($arg in @('build',$project,'--config','Debug')) { $buildInfo.ArgumentList.Add($arg) }
    $buildProcess = [Diagnostics.Process]::Start($buildInfo)
    $buildOut = $buildProcess.StandardOutput.ReadToEndAsync(); $buildErr = $buildProcess.StandardError.ReadToEndAsync()
    $replacement = $null
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $claims = @(Get-ChildItem -LiteralPath $config.acceleration.WorkerQueueDirectory -Filter '*.request.running' -ErrorAction SilentlyContinue)
            if ($claims.Count -gt 0) { break }
            Start-Sleep -Milliseconds 5
        } while ([DateTime]::UtcNow -lt $deadline -and -not $buildProcess.HasExited)
        Check ($claims.Count -eq 1 -and -not $worker.HasExited) 'Could not intercept the worker claim'
        $worker.Kill($true); $worker.WaitForExit()
        $replacement = Start-Worker (Join-Path $vsRoot 'VC/Tools/MSVC')
        Check ($buildProcess.WaitForExit(60000)) 'Recovery build hung'
        $recoveryLog = $buildOut.GetAwaiter().GetResult() + $buildErr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $testRoot 'recovery.log'), $recoveryLog)
        Check ($buildProcess.ExitCode -eq 0 -and $recoveryLog.Contains('requeueing')) "Worker recovery failed: $recoveryLog"
        Check ((& $exe) -eq '41') 'Recovered executable failed'
        Check (@(Get-ChildItem -LiteralPath $config.acceleration.WorkerQueueDirectory -Filter '*.request.running').Count -eq 2) 'Retry reused an old attempt identity'
    } finally {
        foreach ($ownedProcess in @($worker, $replacement, $buildProcess)) {
            if ($null -ne $ownedProcess) { if (-not $ownedProcess.HasExited) { $ownedProcess.Kill($true); $ownedProcess.WaitForExit() }; $ownedProcess.Dispose() }
        }
    }
    $config.acceleration.WorkerTimeoutSeconds = 30
    # Unauthenticated work must not execute, even when it comes from the same filesystem queue.
    $config.acceleration.WorkerQueueDirectory = Join-Path $testRoot 'AuthQueue'
    Save-Config
    $validKey = $env:BEAST_ACCELERATION_TEST_KEY
    $worker = Start-Worker (Join-Path $vsRoot 'VC/Tools/MSVC')
    try {
        $env:BEAST_ACCELERATION_TEST_KEY = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        Beast @('clean', $project)
        $log = (& $tool build $project --config Debug 2>&1 | Out-String)
        Check ($LASTEXITCODE -ne 0 -and $log.Contains('authentication failed')) 'Unauthenticated remote job accepted'
        Check ($worker.WaitForExit(10000)) 'Authentication test worker did not finish'
    } finally {
        $env:BEAST_ACCELERATION_TEST_KEY = $validKey
        if (-not $worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() }; $worker.Dispose()
    }
    $config.acceleration.WorkerQueueDirectory = Join-Path $testRoot 'TimeoutQueue'
    $config.acceleration.WorkerTimeoutSeconds = 1
    Save-Config
    Beast @('clean', $project)
    $log = (& $tool build $project --config Debug 2>&1 | Out-String)
    Check ($LASTEXITCODE -ne 0 -and $log.Contains('timed out')) 'Missing-worker timeout not enforced'
    if ($AndroidSdk) {
        Beast @('module', 'Core', $project)
        [IO.File]::WriteAllText((Join-Path $project 'Source/NativeLib.Target.cs'), @'
using Beast.BuildTool;
public sealed class NativeLibTarget : TargetRules {
    public NativeLibTarget(ModulesInfo info) { Type = TargetType.StaticLibrary; Modules.Add("Core"); }
}
'@)
        $config.android.sdkRoot = $AndroidSdk
        $config.android.ndkRoot = Join-Path $AndroidSdk 'ndk'
        $config.acceleration.WorkerTimeoutSeconds = 30
        $config.acceleration.WorkerQueueDirectory = $mixedQueue
        Save-Config
        $worker = $androidWorker
        $androidWorker = $null
        try {
            Beast @('build', $project, '--target', 'NativeLib', '--platform', 'Android', '--config', 'Release')
            $trace = Get-Content (Join-Path $project 'Intermediate/Build/Android/Release/NativeLib/BuildTrace.json') -Raw | ConvertFrom-Json
            Check (@($trace.actions | Where-Object remote).Count -eq 1) 'Android worker did not compile'
            Check (Test-Path (Join-Path $project 'Binaries/Android/Release/libNativeLib.a')) 'Worker Android archive missing'
            Check ($worker.WaitForExit(10000)) 'Android worker did not finish'
        } finally { if (-not $worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() }; $worker.Dispose() }
    }
} finally {
    if ($androidWorker) {
        if (-not $androidWorker.HasExited) { $androidWorker.Kill($true); $androidWorker.WaitForExit() }
        $androidWorker.Dispose()
    }
    $env:BEAST_ACCELERATION_TEST_KEY = $keyBackup
    $config.acceleration.WorkerQueueDirectory = ''
    $config.acceleration.WorkerKeyEnvironment = 'BEAST_WORKER_KEY'
    $config.acceleration.CacheEnabled = $true
    Save-Config
}
Write-Host "PASS: content hashes, touch/no-op, reverted/optional headers, clean/cache, corruption recovery, shared cache, memory admission, Windows worker, killed-worker retry, authentication, timeout. Android/mixed-queue coverage when -AndroidSdk supplied. Products: $project/Binaries"
