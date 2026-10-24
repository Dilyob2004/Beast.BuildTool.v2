param([switch]$Android, [string]$AndroidSdk = $env:ANDROID_HOME)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tool = Join-Path $root 'BuildTool/bin/Beast.BuildTool.exe'
if (-not (Test-Path $tool)) { throw 'Build BuildTool/Beast.BuildTool.csproj first.' }
$testRoot = Join-Path $PSScriptRoot ('Generated/Smoke ' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $testRoot 'PlainApp'
function Invoke-Beast([string[]]$Arguments) {
    & $tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Beast failed: $Arguments" }
}
function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}
Invoke-Beast @('new', 'PlainApp', '--path', $testRoot)
if ($AndroidSdk) {
    $configPath = Join-Path $project 'Config/BuildConfig.json'
    $settings = Get-Content $configPath -Raw | ConvertFrom-Json
    $settings.android.sdkRoot = $AndroidSdk
    $settings.android.ndkRoot = Join-Path $AndroidSdk 'ndk'
    [IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json -Depth 10))
}
Invoke-Beast @('module', 'Core', $project)
$config = Get-Content (Join-Path $project 'Config/BuildConfig.json') -Raw | ConvertFrom-Json
Assert-True (-not ($config.PSObject.Properties.Name | Where-Object { $_ -in 'engine','hotReload','shaderTools' })) 'Framework settings leaked into config.'
$vcx = [xml](Get-Content (Join-Path $project 'PlainApp.vcxproj') -Raw)
Assert-True ($vcx.Project.PropertyGroup.DebuggerFlavor -contains 'WindowsLocalDebugger') 'Native VS debugger missing.'
Assert-True ($vcx.SelectNodes('//*[local-name()="ProjectReference"]').Count -eq 0) 'Unexpected SDK project reference.'
Assert-True ((Get-Content (Join-Path $project 'PlainApp.vcxproj.filters') -Raw).Contains('Source\Core')) 'Module filters missing.'
Assert-True (-not ((Get-Content (Join-Path $project 'PlainApp.vcxproj') -Raw) -match 'Include="[^"]*\*')) 'Wildcard project items found.'
$header = Join-Path $project 'Source/Core/Public/Core.h'
New-Item -ItemType Directory -Force (Split-Path $header) | Out-Null
[IO.File]::WriteAllText($header, "#pragma once`ninline int CoreValue() { return 42; }`n")
$launch = Join-Path $project 'Source/Launch/Launch.cpp'
$source = [IO.File]::ReadAllText($launch)
$source = $source.Replace('#include <iostream>', "#include <iostream>`n#include `"Core/Public/Core.h`"`n#ifndef MODULE_TEST`n#error Module definitions missing`n#endif")
$source = $source.Replace('"Hello from PlainApp!"', 'CoreValue()')
# Keep Android logging unchanged: only Windows prints CoreValue().
$source = $source.Replace('__android_log_print(ANDROID_LOG_INFO, "PlainApp", CoreValue());', '__android_log_print(ANDROID_LOG_INFO, "PlainApp", "Hello from PlainApp!");')
[IO.File]::WriteAllText($launch, $source)
$rules = Join-Path $project 'Source/Core/Core.Build.cs'
[IO.File]::WriteAllText($rules, @'
using Beast.BuildTool;
public sealed class Core : ModuleRules
{
    public Core(ModulesInfo info) { PublicDefinitions.Add("MODULE_TEST=1"); }
}
'@)
$launchRules = Join-Path $project 'Source/Launch/Launch.Build.cs'
[IO.File]::WriteAllText($launchRules, [IO.File]::ReadAllText($launchRules).Replace('Definitions.Add(', 'PrivateDependencies.Add("Core"); Definitions.Add('))
foreach ($configuration in @('Debug', 'Release')) {
    Invoke-Beast @('build', $project, '--platform', 'Windows', '--config', $configuration)
    $exe = Join-Path $project "Binaries/Windows/$configuration/PlainApp.exe"
    Assert-True ((& $exe) -eq '42') 'Plain executable/module output incorrect.'
    Assert-True ($LASTEXITCODE -eq 0) 'Plain executable failed.'
}
$debugExe = Join-Path $project 'Binaries/Windows/Debug/PlainApp.exe'
$stamp = (Get-Item $debugExe).LastWriteTimeUtc
Invoke-Beast @('build', $project, '--platform', 'Windows', '--config', 'Debug')
Assert-True ((Get-Item $debugExe).LastWriteTimeUtc -eq $stamp) 'Unchanged project unexpectedly relinked.'
[IO.File]::WriteAllText($header, "#pragma once`ninline int CoreValue() { return 43; }`n")
Invoke-Beast @('build', $project, '--platform', 'Windows', '--config', 'Debug')
Assert-True ((& $debugExe) -eq '43') 'Header edit was not rebuilt.'
$goodHeader = [IO.File]::ReadAllText($header)
try {
    [IO.File]::WriteAllText($header, "#error Intentional smoke test failure`n")
    & $tool build $project --platform Windows --config Debug
    Assert-True ($LASTEXITCODE -ne 0) 'Compilation error incorrectly reported success.'
} finally { [IO.File]::WriteAllText($header, $goodHeader) }
Push-Location $project
try { Invoke-Beast @('generate') } finally { Pop-Location }
Invoke-Beast @('clean', $project)
Invoke-Beast @('build', $project, '--platform', 'Windows', '--config', 'Debug')
Assert-True ((& $debugExe) -eq '43') 'Clean rebuild failed.'
Assert-True (-not (Test-Path (Join-Path $project 'Intermediate/Build/BeastSDK'))) 'SDK cache unexpectedly created.'
# An older project with no Target.cs still merges module definitions and discovers all modules.
$targetRules = Join-Path $project 'Source/PlainApp.Target.cs'
$targetBackup = [IO.File]::ReadAllText($targetRules)
try {
    Remove-Item -LiteralPath $targetRules
    [IO.File]::WriteAllText($rules, [IO.File]::ReadAllText($rules).Replace('PublicDefinitions', 'Definitions'))
    Invoke-Beast @('generate', $project)
    Invoke-Beast @('build', $project, '--platform', 'Windows', '--config', 'Debug')
    Assert-True ((& $debugExe) -eq '43') 'Legacy project failed.'
} finally {
    [IO.File]::WriteAllText($targetRules, $targetBackup)
    [IO.File]::WriteAllText($rules, [IO.File]::ReadAllText($rules).Replace('Definitions.Add', 'PublicDefinitions.Add'))
}
if ($Android) {
    foreach ($configuration in @('Debug', 'Release')) {
        Invoke-Beast @('build', $project, '--platform', 'Android', '--config', $configuration)
        $apk = Join-Path $project "Binaries/Android/$configuration/PlainApp.apk"
        Assert-True (Test-Path $apk) 'APK missing.'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($apk)
        try {
            $nativeFiles = @($zip.Entries.FullName | Where-Object { $_ -match '^lib/.*\.so$' })
            Assert-True ($nativeFiles.Count -eq 2) "Unexpected native libraries: $nativeFiles"
            Assert-True ($nativeFiles -contains 'lib/arm64-v8a/libPlainApp.so') 'Application library missing.'
            Assert-True ($nativeFiles -contains 'lib/arm64-v8a/libc++_shared.so') 'C++ runtime missing.'
        } finally { $zip.Dispose() }
        $java = Join-Path $config.android.javaHome 'bin/java.exe'
        $buildTools = Get-ChildItem (Join-Path $config.android.sdkRoot 'build-tools') -Directory | Sort-Object Name -Descending | Select-Object -First 1
        $signer = Join-Path $buildTools.FullName 'lib/apksigner.jar'
        & $java -jar $signer verify $apk
        Assert-True ($LASTEXITCODE -eq 0) 'APK signature invalid.'
    }
}
Write-Host "PASS: standalone smoke tests. Products: $project/Binaries"
