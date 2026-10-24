param([switch]$Android, [string]$AndroidSdk = $env:ANDROID_HOME)
$ErrorActionPreference = 'Stop'
$tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'BuildTool/bin/Beast.BuildTool.exe'
$testRoot = Join-Path $PSScriptRoot ('Generated/Targets ' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $testRoot 'TargetDemo'
function Beast([string[]]$Arguments) {
    & $tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Beast failed: $Arguments" }
}
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
Beast @('new', 'TargetDemo', '--path', $testRoot)
if ($AndroidSdk) {
    $configPath = Join-Path $project 'Config/BuildConfig.json'
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    $config.android.sdkRoot = $AndroidSdk
    $config.android.ndkRoot = Join-Path $AndroidSdk 'ndk'
    [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10))
}
# Preserve the generated Android/resources/config. Replace only the fresh test's Source tree.
$source = [IO.Path]::GetFullPath((Join-Path $project 'Source'))
Check ($source.StartsWith([IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar)) 'Unsafe test path'
Remove-Item -LiteralPath $source -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Fixtures/Targets/Source') -Destination $source -Recurse
Beast @('generate', $project)
$sln = Get-Content (Join-Path $project 'TargetDemo.sln') -Raw
Check ($sln.Contains('MathLibrary.vcxproj') -and $sln.Contains('SharedMath.vcxproj')) 'Targets missing from solution'
$vcx = Get-Content (Join-Path $project 'TargetDemo.vcxproj') -Raw
Check ($vcx.Contains('--target TargetDemo')) 'NMake target missing'
$xml = [xml]$vcx
$main = $xml.SelectSingleNode('//*[local-name()="ClCompile" and @Include="Source\App\Main.cpp"]')
Check ($main.PreprocessorDefinitions[0].InnerText.Contains('MATH_PUBLIC')) 'IntelliSense public definitions missing'
Check (-not $main.PreprocessorDefinitions[0].InnerText.Contains('DETAIL_PUBLIC')) 'IntelliSense private dependency leaked'
foreach ($config in @('Debug', 'Release')) {
    foreach ($target in @('TargetDemo', 'MathLibrary', 'SharedMath')) { Beast @('build', $project, '--target', $target, '--config', $config) }
    $exe = Join-Path $project "Binaries/Windows/$config/TargetDemo.exe"
    Check ((& $exe) -eq '42') 'Wrong target output'
    Check (Test-Path (Join-Path $project "Binaries/Windows/$config/MathLibrary.lib")) 'Static library missing'
    Check (Test-Path (Join-Path $project "Binaries/Windows/$config/SharedMath.dll")) 'Shared library missing'
}
$exe = Join-Path $project 'Binaries/Windows/Debug/TargetDemo.exe'
$stamp = (Get-Item $exe).LastWriteTimeUtc
Beast @('build', $project, '--config', 'Debug')
Check ((Get-Item $exe).LastWriteTimeUtc -eq $stamp) 'No-op target relinked'
$helper = Join-Path $source 'Helper/Public/Helper.h'
$helperOriginal = [IO.File]::ReadAllText($helper)
try {
    [IO.File]::WriteAllText($helper, $helperOriginal.Replace('return 30', 'return 31'))
    Beast @('build', $project, '--config', 'Debug')
    Check ((& $exe) -eq '43') 'Compiler-emitted angle-bracket header dependency not rebuilt'
    $trace = Get-Content (Join-Path $project 'Intermediate/Build/Windows/Debug/TargetDemo/BuildTrace.json') -Raw | ConvertFrom-Json
    Check (@($trace.actions | Where-Object { $_.description -eq 'Compiling Detail.cpp ..' -and $_.skipped }).Count -eq 1) 'Unrelated module unnecessarily rebuilt'
} finally { [IO.File]::WriteAllText($helper, $helperOriginal) }
$detailRules = Join-Path $source 'Detail/Detail.Build.cs'
$original = [IO.File]::ReadAllText($detailRules)
try {
    foreach ($dependency in @('App', 'MissingModule')) {
        [IO.File]::WriteAllText($detailRules, $original.Replace('PublicIncludePaths.Add(', "PrivateDependencies.Add(`"$dependency`"); PublicIncludePaths.Add("))
        $log = (& $tool build $project --target TargetDemo 2>&1 | Out-String)
        Check ($LASTEXITCODE -ne 0) 'Invalid dependency accepted'
        Check ($log.Contains($(if ($dependency -eq 'App') { 'dependency cycle' } else { 'unknown module' }))) 'Missing dependency diagnostic'
    }
} finally { [IO.File]::WriteAllText($detailRules, $original) }
# Replacing rule content with an older timestamp must invalidate cached C# rules.
$oldStamp = (Get-Item $detailRules).LastWriteTimeUtc
try {
    [IO.File]::WriteAllText($detailRules, $original.Replace('DETAIL_PRIVATE=1', 'DETAIL_PRIVATE=2'))
    (Get-Item $detailRules).LastWriteTimeUtc = $oldStamp.AddDays(-1)
    Beast @('build', $project, '--target', 'TargetDemo')
    Beast @('generate', $project)
    Check ((Get-Content (Join-Path $project 'TargetDemo.vcxproj') -Raw).Contains('DETAIL_PRIVATE=2')) 'Rules content change not applied'
} finally { [IO.File]::WriteAllText($detailRules, $original) }
if ($Android) {
    foreach ($target in @('MathLibrary', 'SharedMath')) { Beast @('build', $project, '--target', $target, '--platform', 'Android', '--config', 'Release') }
    Check (Test-Path (Join-Path $project 'Binaries/Android/Release/libMathLibrary.a')) 'Android archive missing'
    Check (Test-Path (Join-Path $project 'Binaries/Android/Release/libSharedMath.so')) 'Android shared library missing'
    Check (-not (Test-Path (Join-Path $project 'Binaries/Android/Release/TargetDemo.apk'))) 'Library target unexpectedly packaged APK'
    $so = Join-Path $project 'Binaries/Android/Release/libSharedMath.so'
    $stamp = (Get-Item $so).LastWriteTimeUtc
    Beast @('build', $project, '--target', 'SharedMath', '--platform', 'Android', '--config', 'Release')
    Check ((Get-Item $so).LastWriteTimeUtc -eq $stamp) 'No-op Android library relinked'
    try {
        [IO.File]::WriteAllText($helper, $helperOriginal.Replace('return 30', 'return 31'))
        Beast @('build', $project, '--target', 'SharedMath', '--platform', 'Android', '--config', 'Release')
        Check ((Get-Item $so).LastWriteTimeUtc -gt $stamp) 'Android header edit not linked'
        $trace = Get-Content (Join-Path $project 'Intermediate/Build/Android/Release/SharedMath/BuildTrace.json') -Raw | ConvertFrom-Json
        Check (@($trace.actions | Where-Object { $_.description -eq 'Compiling Math.cpp for Android...' -and -not $_.skipped }).Count -eq 1) 'Wrong Android header rebuild set'
        Check (@($trace.actions | Where-Object { $_.description -eq 'Compiling Detail.cpp for Android...' -and $_.skipped }).Count -eq 1) 'Unrelated Android module rebuilt'
    } finally { [IO.File]::WriteAllText($helper, $helperOriginal) }
}
Write-Host "PASS: target/module integration tests. Products: $project/Binaries"
