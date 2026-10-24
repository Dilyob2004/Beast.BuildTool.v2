param([string]$AndroidSdk = 'C:/Users/dilyo/AppData/Local/Android/Sdk')
$ErrorActionPreference = 'Stop'
$tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'BuildTool/bin/Beast.BuildTool.exe'
$root = Join-Path $PSScriptRoot ('Generated/Android Packaging ' + [guid]::NewGuid().ToString('N'))
function Check([bool]$value,[string]$message) { if (-not $value) { throw $message } }
function Beast([string[]]$arguments) {
    Write-Host "RUN: $arguments"
    $start = [Diagnostics.ProcessStartInfo]::new($tool)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw 'Beast test command timed out' }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Beast failed: $arguments`n$output" }
    } finally { $process.Dispose() }
    return $output
}
Beast @('new','PackageApp','--path',$root) | Out-Null
$project = Join-Path $root 'PackageApp'
$configPath = Join-Path $project 'Config/BuildConfig.json'
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$config.android.sdkRoot = $AndroidSdk
$config.android.ndkRoot = Join-Path $AndroidSdk 'ndk'
# Tiny single-TU fixture: leave room for Gradle on a busy developer machine.
$config.maxParallelJobs = 1
$config.acceleration.MemoryReserveMB = 256
$config.acceleration.CompileMemoryMB = 128
$config.acceleration.LinkMemoryMB = 256
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10))
$source = Join-Path $project 'Source/Launch'
$native = Join-Path $project 'Source/Native'
New-Item -ItemType Directory -Path $native,(Join-Path $source 'Assets/Nested'),(Join-Path $project 'Platforms/Android/app/src/main/assets') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $native 'Native.Build.cs'), 'using Beast.BuildTool; public sealed class Native : ModuleRules {public Native(ModulesInfo info) {}}')
[IO.File]::WriteAllText((Join-Path $native 'Native.cpp'), 'extern "C" __attribute__((visibility("default"))) int NativeValue() {return 42;}')
[IO.File]::WriteAllText((Join-Path $project 'Source/Extra.Target.cs'), 'using Beast.BuildTool; public sealed class ExtraTarget : TargetRules {public ExtraTarget(ModulesInfo info) {Type=TargetType.SharedLibrary; Modules.Add("Native");}}')
[IO.File]::WriteAllText((Join-Path $source 'Assets/Nested/data.txt'), 'first')
[IO.File]::WriteAllText((Join-Path $source 'seed.txt'), 'generated-v1')
[IO.File]::WriteAllText((Join-Path $source 'Generate.ps1'), @'
param([string]$InputPath,[string]$OutputPath)
[IO.File]::WriteAllText($OutputPath, [IO.File]::ReadAllText($InputPath))
'@)
$userAsset = Join-Path $project 'Platforms/Android/app/src/main/assets/user.txt'
[IO.File]::WriteAllText($userAsset, 'user-owned')
$gradle = Join-Path $project 'Platforms/Android/app/build.gradle'
$gradleHash = (Get-FileHash $gradle).Hash
$rulesPath = Join-Path $source 'Launch.Build.cs'
$rules = @'
using Beast.BuildTool;
public sealed class Launch : ModuleRules {
    public Launch(ModulesInfo info) {
        if (info.Platform != PlatformType.Android) return;
        var generate = new BuildStep {Name="Payload", Executable="pwsh", AffectsCompilation=false};
        generate.Arguments.AddRange(new[] {"-NoProfile","-File","{ModuleDir}/Generate.ps1","{ModuleDir}/seed.txt","{GeneratedDir}/payload.txt"});
        generate.Inputs.AddRange(new[] {"Generate.ps1","seed.txt"});
        generate.Outputs.Add("payload.txt");
        BuildSteps.Add(generate);
        AndroidAssets.Add("Assets", "Data");
        AndroidAssets.Add("{GeneratedDir}/payload.txt", "Config/payload.txt");
        AndroidNativeLibraries.Add("{BinaryDir}/libExtra.so");
    }
}
'@
[IO.File]::WriteAllText($rulesPath,$rules)
$buildTools = Get-ChildItem (Join-Path $AndroidSdk 'build-tools') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$signer = Join-Path $buildTools.FullName 'lib/apksigner.jar'
$java = Join-Path $config.android.javaHome 'bin/java.exe'
function ZipText($zip,[string]$name) {
    $entry = $zip.GetEntry($name)
    Check ($null -ne $entry) "Missing APK entry: $name"
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Build-App([string]$stage) {
    Beast @('build',$project,'--target','PackageApp','--platform','Android','--config',$configuration) | Set-Content (Join-Path $root "$configuration-$stage.log")
    $apk = Join-Path $project "Binaries/Android/$configuration/PackageApp.apk"
    & $java -jar $signer verify $apk
    Check ($LASTEXITCODE -eq 0) 'Invalid APK signature'
    Check ((Get-FileHash $gradle).Hash -eq $gradleHash) 'User build.gradle changed'
    Check ((Get-Content $userAsset -Raw) -eq 'user-owned') 'User asset modified'
    return $apk
}
foreach ($configuration in @('Debug','Release')) {
    Beast @('build',$project,'--target','Extra','--platform','Android','--config',$configuration) | Set-Content (Join-Path $root "$configuration-library.log")
    $library = Join-Path $project "Binaries/Android/$configuration/libExtra.so"
    $libraryHash = (Get-FileHash $library).Hash
    $apk = Build-App 'cold'
    $zip = [IO.Compression.ZipFile]::OpenRead($apk)
    try {
        Check ((ZipText $zip 'assets/Data/Nested/data.txt') -eq 'first') 'Directory asset wrong'
        Check ((ZipText $zip 'assets/Config/payload.txt') -eq 'generated-v1') 'Generated asset wrong'
        Check ((ZipText $zip 'assets/user.txt') -eq 'user-owned') 'Existing Gradle assets lost'
        Check ($null -ne $zip.GetEntry('lib/arm64-v8a/libExtra.so')) 'Native dependency absent'
    } finally { $zip.Dispose() }
    Check ((Get-FileHash $library).Hash -eq $libraryHash) 'Release stripping modified source library'
    if ($configuration -eq 'Release') {
        $symbols = Join-Path $project 'Binaries/Android/Release/Symbols/arm64-v8a'
        Check ((Get-FileHash (Join-Path $symbols 'libExtra.so')).Hash -eq $libraryHash) 'Unstripped extra library not preserved'
        Check (@((Get-Content (Join-Path $symbols 'manifest.json') -Raw | ConvertFrom-Json).libraries | Where-Object library -eq 'libExtra.so').Count -eq 1) 'Extra library absent in symbols manifest'
    }
    Build-App 'noop' | Out-Null
    $trace = Get-Content (Join-Path $project "Intermediate/Build/Android/$configuration/PackageApp/BuildTrace.json") -Raw | ConvertFrom-Json
    Check (@($trace.actions | Where-Object { -not $_.skipped }).Count -eq 0) 'APK graph no-op executed actions'
    Write-Host "PASS: $configuration APK assets/native libraries, signature, no-op, user files and original symbols"
}
$configuration = 'Release'
$seed = Join-Path $source 'seed.txt'
$stamp = (Get-Item $seed).LastWriteTimeUtc
[IO.File]::WriteAllText($seed,'generated-v2'); (Get-Item $seed).LastWriteTimeUtc = $stamp
$apk = Build-App 'generated-edit'
$zip = [IO.Compression.ZipFile]::OpenRead($apk)
try { Check ((ZipText $zip 'assets/Config/payload.txt') -eq 'generated-v2') 'Generated asset stale in APK' } finally { $zip.Dispose() }
$trace = Get-Content (Join-Path $project 'Intermediate/Build/Android/Release/PackageApp/BuildTrace.json') -Raw | ConvertFrom-Json
Check (@($trace.actions | Where-Object { -not $_.skipped -and $_.description -notlike 'Generating*' -and $_.description -notlike 'Staging APK*' }).Count -eq 0) 'Asset edit rebuilt C++'
# A removed rule must disappear from APK without deleting any source file.
[IO.File]::WriteAllText($rulesPath,$rules.Replace('AndroidAssets.Add("Assets", "Data");','').Replace('AndroidNativeLibraries.Add("{BinaryDir}/libExtra.so");',''))
$apk = Build-App 'removed'
$zip = [IO.Compression.ZipFile]::OpenRead($apk)
try {
    Check ($null -eq $zip.GetEntry('assets/Data/Nested/data.txt')) 'Removed asset leaked into APK'
    Check ($null -eq $zip.GetEntry('lib/arm64-v8a/libExtra.so')) 'Removed native library leaked into APK'
    Check ((ZipText $zip 'assets/user.txt') -eq 'user-owned') 'Removal damaged user assets'
} finally { $zip.Dispose() }
Check (Test-Path (Join-Path $source 'Assets/Nested/data.txt')) 'Original asset was deleted'
function Reject([string]$text,[string]$expected) {
    [IO.File]::WriteAllText($rulesPath,$text)
    $output = (& $tool build $project --target PackageApp --platform Android --config Release 2>&1 | Out-String)
    Check ($LASTEXITCODE -ne 0 -and $output.Contains($expected)) "Expected $expected : $output"
}
Reject ($rules.Replace('"Data"','"../escape"')) 'Invalid APK destination'
Reject ($rules.Replace('AndroidNativeLibraries.Add("{BinaryDir}/libExtra.so");','AndroidNativeLibraries.Add("{BinaryDir}/libExtra.so", "libPackageApp.so");')) 'Reserved Android native library'
Reject ($rules.Replace('AndroidAssets.Add("Assets", "Data");','AndroidAssets.Add("Assets", "Data"); AndroidAssets.Add("seed.txt", "Data/Nested/data.txt");')) 'Conflicting APK destination'
Reject ($rules.Replace('"{BinaryDir}/libExtra.so"','"seed.txt", "libFake.so"')) 'ELF64 little-endian ARM64'
Reject ($rules.Replace('"{BinaryDir}/libExtra.so"','"missing.so", "libMissing.so"')) 'Missing input'
[IO.File]::WriteAllText($rulesPath,$rules)
Write-Host "PASS: APK generation/update/removal and negative validation. Fixture: $project"
