param([switch]$Android, [string]$AndroidSdk = $env:ANDROID_HOME)
$ErrorActionPreference = 'Stop'
$tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'BuildTool/bin/Beast.BuildTool.exe'
$root = Join-Path $PSScriptRoot ('Generated/BuildSteps ' + [guid]::NewGuid().ToString('N'))
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Beast([string[]]$Arguments) {
    $output = (& $tool @Arguments 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Beast failed: $Arguments`n$output" }
    return $output
}
Beast @('new','StepsApp','--path',$root) | Out-Null
$project = Join-Path $root 'StepsApp'
$source = Join-Path $project 'Source/Launch'
$inputs = Join-Path $source 'Inputs'
$headerModule = Join-Path $project 'Source/GeneratedHeaders'
New-Item -ItemType Directory -Path $headerModule -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $headerModule 'MakeHeader.ps1'), @'
param([string]$Output)
[IO.File]::WriteAllText($Output, "#pragma once`n#define DEPENDENCY_VALUE 7`n")
'@)
[IO.File]::WriteAllText((Join-Path $headerModule 'GeneratedHeaders.Build.cs'), @'
using Beast.BuildTool;
public sealed class GeneratedHeaders : ModuleRules {
    public GeneratedHeaders(ModulesInfo info) {
        Type = ModuleType.HeaderOnly;
        PublicIncludePaths.Add("{GeneratedDir}/include");
        var header = new BuildStep { Name = "Header", Executable = "pwsh" };
        header.Arguments.AddRange(new[] { "-NoProfile", "-File", "{ModuleDir}/MakeHeader.ps1", "{GeneratedDir}/include/Definition.h" });
        header.Inputs.Add("MakeHeader.ps1");
        header.Outputs.Add("include/Definition.h");
        BuildSteps.Add(header);
    }
}
'@)
New-Item -ItemType Directory -Path (Join-Path $inputs 'Assets/Nested') -Force | Out-Null
$schema = Join-Path $inputs 'Schema.txt'
[IO.File]::WriteAllText($schema, '41')
[IO.File]::WriteAllText((Join-Path $inputs 'settings.json'), '{"theme":"green"}')
[IO.File]::WriteAllText((Join-Path $inputs 'payload-tag.txt'), 'v1')
[IO.File]::WriteAllText((Join-Path $inputs 'Assets/Nested/icon.dat'), 'asset-v1')
[IO.File]::WriteAllText((Join-Path $source 'MakeCode.ps1'), @'
param([string]$Schema,[string]$Output,[string]$Literal)
$ErrorActionPreference = 'Stop'
if ($Literal -ne '%PATH% & literal') { throw 'Arguments were interpreted by a shell' }
if ($env:BEAST_STEP_TEST -ne 'configured') { throw 'Step environment was not applied' }
$number = [int][IO.File]::ReadAllText($Schema)
[IO.File]::WriteAllText((Join-Path $Output 'Generated.h'), "#pragma once`n#define GENERATED_VALUE $number`nint GeneratedValue();`n")
[IO.File]::WriteAllText((Join-Path $Output 'Generated.cpp'), "#include `"Generated.h`"`nint GeneratedValue() {return GENERATED_VALUE;}`n")
[IO.File]::WriteAllText((Join-Path $Output 'generated.json'), "{`"value`":$number}")
'@)
[IO.File]::WriteAllText((Join-Path $source 'CopyPayload.ps1'), @'
param([string]$InputPath,[string]$OutputPath,[string]$TagPath)
$ErrorActionPreference = 'Stop'
$value = [IO.File]::ReadAllText($InputPath) | ConvertFrom-Json
[IO.File]::WriteAllText($OutputPath, (@{value=$value.value; tag=[IO.File]::ReadAllText($TagPath)} | ConvertTo-Json -Compress))
'@)
$rulesPath = Join-Path $source 'Launch.Build.cs'
$rules = @'
using Beast.BuildTool;
public sealed class Launch : ModuleRules {
    public Launch(ModulesInfo info) {
        PrivateDependencies.Add("GeneratedHeaders");
        var code = new BuildStep { Name = "Schema", Executable = "pwsh" };
        code.Arguments.AddRange(new[] { "-NoProfile", "-File", "{ModuleDir}/MakeCode.ps1", "{ModuleDir}/Inputs/Schema.txt", "{GeneratedDir}", "%PATH% & literal" });
        code.Inputs.AddRange(new[] { "MakeCode.ps1", "Inputs/Schema.txt" });
        code.Outputs.AddRange(new[] { "Generated.h", "Generated.cpp", "generated.json" });
        code.Environment.Add("BEAST_STEP_TEST", "configured");
        BuildSteps.Add(code);
        var payload = new BuildStep { Name = "Payload", Executable = "pwsh", AffectsCompilation = false };
        payload.Arguments.AddRange(new[] { "-NoProfile", "-File", "{ModuleDir}/CopyPayload.ps1", "{GeneratedDir}/generated.json", "{GeneratedDir}/deployed.json", "{ModuleDir}/Inputs/payload-tag.txt" });
        payload.Inputs.AddRange(new[] { "CopyPayload.ps1", "{GeneratedDir}/generated.json", "Inputs/payload-tag.txt" });
        payload.Outputs.Add("deployed.json");
        BuildSteps.Add(payload);
        GeneratedSources.Add("Generated.cpp");
        PrecompiledHeaderSource = "{GeneratedDir}/Generated.h";
        UseUnityBuild = true; UnityBatchSize = 4;
        RuntimeDependencies.Add("Inputs/settings.json", "Config/settings.json");
        RuntimeDependencies.Add("Inputs/Assets", "Assets");
        RuntimeDependencies.Add("{GeneratedDir}/deployed.json", "Config/generated.json");
        RuntimeDependencies.Add(typeof(ModuleRules).Assembly.Location);
    }
}
'@
[IO.File]::WriteAllText($rulesPath, $rules)
[IO.File]::WriteAllText((Join-Path $source 'Launch.cpp'), "#include `"Generated.h`"`n#include `"Definition.h`"`n#include <cstdio>`nstatic_assert(DEPENDENCY_VALUE == 7);`nint main() {printf(`"%d`", GeneratedValue());}`n")
$targetPath = Join-Path $project 'Source/StepsApp.Target.cs'
$target = @'
using Beast.BuildTool;
public sealed class StepsAppTarget : TargetRules {
    public StepsAppTarget(ModulesInfo info) { Type = TARGET_KIND; Modules.Add("Launch"); }
}
'@
$configPath = Join-Path $project 'Config/BuildConfig.json'
$config = Get-Content $configPath -Raw | ConvertFrom-Json
if ($AndroidSdk) { $config.android.sdkRoot = $AndroidSdk; $config.android.ndkRoot = Join-Path $AndroidSdk 'ndk' }
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10))
$platforms = @('Windows'); if ($Android) { $platforms += 'Android' }
foreach ($platform in $platforms) {
    [IO.File]::WriteAllText($schema, '41')
    [IO.File]::WriteAllText((Join-Path $inputs 'settings.json'), '{"theme":"green"}')
    [IO.File]::WriteAllText((Join-Path $inputs 'payload-tag.txt'), 'v1')
    [IO.File]::WriteAllText($rulesPath, $rules)
    [IO.File]::WriteAllText($targetPath, $target.Replace('TARGET_KIND', $(if ($platform -eq 'Windows') {'TargetType.Application'} else {'TargetType.StaticLibrary'})))
    $trace = Join-Path $project "Intermediate/Build/$platform/Debug/StepsApp/BuildTrace.json"
    $binary = Join-Path $project "Binaries/$platform/Debug"
    function Build([string]$stage) {
        Beast @('build',$project,'--platform',$platform,'--config','Debug') | Set-Content (Join-Path $root "$platform-$stage.log")
        Copy-Item $trace (Join-Path $root "$platform-$stage.trace.json")
    }
    function Actions { (Get-Content $trace -Raw | ConvertFrom-Json).actions }
    function Executed { @(Actions | Where-Object { -not $_.skipped -and -not $_.cacheHit }) }
    Build 'cold'
    Check ((Get-Content (Join-Path $binary 'Config/generated.json') -Raw | ConvertFrom-Json).value -eq 41) 'Generated runtime payload wrong'
    Check ((Get-Content (Join-Path $binary 'Assets/Nested/icon.dat') -Raw) -eq 'asset-v1') 'Directory mapping not preserved'
    Check ((Get-FileHash (Join-Path $binary 'Beast.BuildTool.dll')).Hash -eq (Get-FileHash ([IO.Path]::ChangeExtension($tool, '.dll'))).Hash) 'Default DLL destination or bytes wrong'
    if ($platform -eq 'Windows') {
        Check ((& (Join-Path $binary 'StepsApp.exe')) -eq '41') 'Generated C++ executable wrong'
        Beast @('generate',$project) | Out-Null
        Check ((Get-Content (Join-Path $project 'StepsApp.vcxproj') -Raw).Contains('Custom')) 'Generated include path missing in VS'
    }
    Build 'noop'; Check ((Executed).Count -eq 0) "$platform no-op reran actions"
    $stamp = (Get-Item $schema).LastWriteTimeUtc
    [IO.File]::WriteAllText($schema, '42'); (Get-Item $schema).LastWriteTimeUtc = $stamp
    Build 'schema-change'
    Check (@(Executed | Where-Object { $_.description -like 'Generating*' }).Count -eq 2) 'Generator chain not rebuilt'
    Check ((Get-Content (Join-Path $binary 'Config/generated.json') -Raw | ConvertFrom-Json).value -eq 42) 'Runtime generated file stale'
    if ($platform -eq 'Windows') { Check ((& (Join-Path $binary 'StepsApp.exe')) -eq '42') 'Generated header/PCH edit did not reach EXE' }
    [IO.File]::WriteAllText((Join-Path $inputs 'settings.json'), '{"theme":"blue"}')
    Build 'runtime-change'
    Check (@(Executed | Where-Object { $_.description -notlike 'Copying runtime*' }).Count -eq 0) 'Runtime-only change rebuilt code'
    Check ((Executed).Count -eq 1) 'Expected one runtime copy'
    [IO.File]::WriteAllText((Join-Path $inputs 'payload-tag.txt'), 'v2')
    Build 'asset-generator-change'
    Check ((Get-Content (Join-Path $binary 'Config/generated.json') -Raw | ConvertFrom-Json).tag -eq 'v2') 'Generated asset not updated'
    Check ((Executed).Count -eq 2 -and @(Executed | Where-Object { $_.description -notlike 'Generating Launch/Payload*' -and $_.description -notlike 'Copying runtime*' }).Count -eq 0) 'Asset generator unnecessarily rebuilt code'
    [IO.File]::WriteAllText((Join-Path $binary 'Config/settings.json'), 'damaged')
    Build 'runtime-repair'
    Check ((Get-Content (Join-Path $binary 'Config/settings.json') -Raw).Contains('blue')) 'Damaged runtime output not repaired'
    [IO.File]::WriteAllText((Join-Path $inputs 'Assets/Nested/new.dat'), 'new asset')
    Build 'runtime-add'
    Check (Test-Path (Join-Path $binary 'Assets/Nested/new.dat')) 'New directory member not staged'
    $generated = Join-Path $project "Intermediate/Build/$platform/Debug/StepsApp/Generated/Launch/Custom/Generated.cpp"
    Remove-Item -LiteralPath $generated
    Build 'missing-output'
    Check (@(Executed | Where-Object { $_.description -like 'Generating Launch/Schema*' }).Count -eq 1) 'Deleted generated file not recreated'
    Build 'final-noop'; Check ((Executed).Count -eq 0) 'Repaired project not up to date'
    Write-Host "PASS: $platform generators, generated PCH/Unity, copy, no-op and changes"
}
# Negative rules must fail without running an unsafe command or publishing success.
[IO.File]::WriteAllText($targetPath, $target.Replace('TARGET_KIND','TargetType.Application'))
function Reject-Rules([string]$text,[string]$expected) {
    [IO.File]::WriteAllText($rulesPath,$text)
    $failure = (& $tool build $project --platform Windows --config Debug 2>&1 | Out-String)
    Check ($LASTEXITCODE -ne 0 -and $failure.Contains($expected)) "Expected '$expected': $failure"
}
Reject-Rules ($rules.Replace('Inputs/settings.json','Inputs/missing.json')) 'Missing input'
Reject-Rules ($rules.Replace('Config/settings.json','../outside.json')) 'escapes its allowed'
Reject-Rules ($rules.Replace('code.Outputs.AddRange(new[] { "Generated.h", "Generated.cpp", "generated.json" });','code.Outputs.AddRange(new[] { "../escaped.h", "Generated.cpp", "generated.json" });')) 'escapes its allowed'
Reject-Rules ($rules.Replace('code.Inputs.AddRange(new[] { "MakeCode.ps1", "Inputs/Schema.txt" });','code.Inputs.AddRange(new[] { "MakeCode.ps1", "Inputs/Schema.txt", "{GeneratedDir}/deployed.json" });')) 'cycle'
Reject-Rules ($rules.Replace('Config/settings.json','StepsApp.exe')) 'Multiple actions produce'
Reject-Rules ($rules.Replace('Config/settings.json','StepsApp.pdb.state.json')) 'state path conflicts'
# A successful process that omits an output must not reuse an old artifact/state.
Reject-Rules ($rules.Replace('"Generated.h", "Generated.cpp", "generated.json"','"Generated.h", "Generated.cpp", "generated.json", "missing.txt"')) 'Command did not produce'
Check (-not (Test-Path (Join-Path $project 'Binaries/Windows/Debug/missing.txt'))) 'Missing output incorrectly published'
[IO.File]::WriteAllText($rulesPath,$rules)
$generatorPath = Join-Path $source 'MakeCode.ps1'
$originalGenerator = [IO.File]::ReadAllText($generatorPath)
try {
    [IO.File]::WriteAllText($generatorPath, "throw 'intentional generator failure'")
    Reject-Rules $rules 'Build failed: Generating Launch/Schema'
    Check (-not (Test-Path (Join-Path $project 'Intermediate/Build/Windows/Debug/StepsApp/Generated/Launch/Custom/Generated.cpp'))) 'Failed generator retained stale declared output'
} finally { [IO.File]::WriteAllText($generatorPath,$originalGenerator) }
Beast @('build',$project,'--platform','Windows','--config','Debug') | Out-Null
Check ((& (Join-Path $project 'Binaries/Windows/Debug/StepsApp.exe')) -eq '42') 'Recovery after failed generator failed'
Write-Host "PASS: build steps/runtime integration and negative validation. Products: $root"
