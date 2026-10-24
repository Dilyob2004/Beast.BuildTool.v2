param([string]$AndroidSdk = 'C:/Users/dilyo/AppData/Local/Android/Sdk')
$ErrorActionPreference = 'Stop'
$tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'BuildTool/bin/Beast.BuildTool.exe'
$root = Join-Path $PSScriptRoot ('Generated/Hardening ' + [guid]::NewGuid().ToString('N'))
function Beast([string[]]$Arguments) {
    $output = (& $tool @Arguments 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Beast failed: $Arguments`n$output" }
    return $output
}
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
Beast @('new','ResourcesApp','--path',$root) | Out-Null
$project = Join-Path $root 'ResourcesApp'
$tracePath = Join-Path $project 'Intermediate/Build/Windows/Debug/ResourcesApp/BuildTrace.json'
function Trace { (Get-Content $tracePath -Raw | ConvertFrom-Json).actions }
[IO.File]::WriteAllText((Join-Path $project 'Source/Launch/Launch.cpp'), '__declspec(deprecated("BEAST_WARNING_VISIBLE")) int Old() { return 0; } int main() { return Old(); }')
$header = Join-Path $project 'Platforms/Windows/Version.h'
$payload = Join-Path $project 'Platforms/Windows/custom.data'
[IO.File]::WriteAllText($header, "#define TEST_RESOURCE 20`n")
[IO.File]::WriteAllText($payload, 'resource-one')
$rc = Join-Path $project 'Platforms/Windows/Resources/ResourcesApp.rc'
[IO.File]::AppendAllText($rc, "`n#include `"../Version.h`"`n201 RCDATA BEGIN TEST_RESOURCE END`n202 RCDATA `"../custom.data`"`n")
$output = Beast @('build',$project)
Check ($output.Contains('BEAST_WARNING_VISIBLE')) 'Successful compiler warning was swallowed'
Beast @('build',$project) | Out-Null
Check (@(Trace | Where-Object { -not $_.skipped }).Count -eq 0) 'RC no-op rebuilt'
$stamp = (Get-Item $header).LastWriteTimeUtc
[IO.File]::WriteAllText($header, "#define TEST_RESOURCE 21`n")
(Get-Item $header).LastWriteTimeUtc = $stamp
Beast @('build',$project) | Out-Null
Check (@(Trace | Where-Object { $_.description -like 'Compiling Windows resources*' -and -not $_.skipped }).Count -eq 1) 'Nested RC header was not rebuilt'
Check (@(Trace | Where-Object { $_.description -like 'Compiling Launch*' -and -not $_.skipped }).Count -eq 0) 'RC header rebuilt C++'
[IO.File]::WriteAllText($payload, 'resource-two')
Beast @('build',$project) | Out-Null
Check (@(Trace | Where-Object { $_.description -like 'Compiling Windows resources*' -and -not $_.skipped }).Count -eq 1) 'External resource payload was not rebuilt'
$configPath = Join-Path $project 'Config/BuildConfig.json'
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$config.acceleration.MaxMemoryMB = 1
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10))
Beast @('build',$project) | Out-Null
Check (@(Trace | Where-Object { -not $_.skipped }).Count -eq 0) 'No-op required compiler memory'

$shared = Join-Path $root 'PortableShared'
$objects = @()
foreach ($copy in @('A','B')) {
    $parent = Join-Path $root $copy
    Beast @('new','PortableApp','--path',$parent) | Out-Null
    $portable = Join-Path $parent 'PortableApp'
    Beast @('module','Core',$portable) | Out-Null
    [IO.File]::WriteAllText((Join-Path $portable 'Source/Core/Core.cpp'), 'const char* SourceFile() { return __FILE__; }')
    [IO.File]::WriteAllText((Join-Path $portable 'Source/NativeLib.Target.cs'), @'
using Beast.BuildTool;
public sealed class NativeLibTarget : TargetRules {
    public NativeLibTarget(ModulesInfo info) { Type = TargetType.StaticLibrary; Modules.Add("Core"); }
}
'@)
    $pc = Join-Path $portable 'Config/BuildConfig.json'
    $settings = Get-Content $pc -Raw | ConvertFrom-Json
    $settings.acceleration.PortableClangCache = $true
    $settings.acceleration.SharedCacheDirectory = $shared
    $settings.android.sdkRoot = $AndroidSdk
    $settings.android.ndkRoot = Join-Path $AndroidSdk 'ndk'
    [IO.File]::WriteAllText($pc, ($settings | ConvertTo-Json -Depth 10))
    Beast @('build',$portable,'--target','NativeLib','--platform','Android','--config','Release') | Out-Null
    $pt = Get-Content (Join-Path $portable 'Intermediate/Build/Android/Release/NativeLib/BuildTrace.json') -Raw | ConvertFrom-Json
    if ($copy -eq 'B') { Check (@($pt.actions | Where-Object cacheHit).Count -eq 1) 'Relocated Clang project did not hit portable shared cache' }
    $obj = Get-ChildItem (Join-Path $portable 'Intermediate/Build') -Filter '*.o' -Recurse | Select-Object -First 1
    $objects += (Get-FileHash $obj.FullName).Hash
    $bytes = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($obj.FullName))
    Check ($bytes.Contains('/__beast_project')) 'Clang did not map embedded source paths'
    Beast @('build',$portable,'--target','NativeLib','--platform','Android','--config','Release') | Out-Null
    $pt = Get-Content (Join-Path $portable 'Intermediate/Build/Android/Release/NativeLib/BuildTrace.json') -Raw | ConvertFrom-Json
    Check (@($pt.actions | Where-Object { -not $_.skipped }).Count -eq 0) 'Restored depfile failed no-op at relocated path'
}
Check ($objects[0] -eq $objects[1]) 'Portable object bytes differ'
Write-Host "PASS: successful warnings, external/nested RC dependencies, low-memory no-op, relocated Clang shared-cache object and depfile. Products: $root"
