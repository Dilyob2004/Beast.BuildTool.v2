param([switch]$Android, [string]$AndroidSdk = $env:ANDROID_HOME)
$ErrorActionPreference = 'Stop'
$tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'BuildTool/bin/Beast.BuildTool.exe'
$root = Join-Path $PSScriptRoot ('Generated/CompilationModes ' + [guid]::NewGuid().ToString('N'))
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Beast([string[]]$Arguments) {
    $output = (& $tool @Arguments 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Beast failed: $Arguments`n$output" }
    return $output
}
$platforms = @('Windows'); if ($Android) { $platforms += 'Android' }
foreach ($platform in $platforms) {
    foreach ($mode in @('Plain','Pch','Unity','Both')) {
        $usePch = $mode -in @('Pch','Both'); $useUnity = $mode -in @('Unity','Both')
        $parent = Join-Path $root "$platform/$mode"
        Beast @('new','Modes','--path',$parent) | Out-Null
        $project = Join-Path $parent 'Modes'
        $source = Join-Path $project 'Source/Launch'
        $header = Join-Path $source 'Value.h'
        [IO.File]::WriteAllText($header, "#pragma once`n#define VALUE 7`n")
        [IO.File]::WriteAllText((Join-Path $source 'Pch.h'), "#pragma once`n#include <vector>`n#include <array>`n#include `"Value.h`"`n#define PCH_ACTIVE 1`n")
        $rules = @'
using Beast.BuildTool;
public sealed class Launch : ModuleRules {
    public Launch(ModulesInfo info) {
        PrecompiledHeaderSource = PCH_HEADER;
        UseUnityBuild = UNITY_ENABLED; UnityBatchSize = 4;
        UnityExcludedFiles.Add("Isolated.cpp");
        PrecompiledHeaderExcludedFiles.Add("Isolated.cpp");
    }
}
'@
        $rules = $rules.Replace('PCH_HEADER', $(if ($usePch) { '"Pch.h"' } else { '""' })).Replace('UNITY_ENABLED', $useUnity.ToString().ToLowerInvariant())
        [IO.File]::WriteAllText((Join-Path $source 'Launch.Build.cs'), $rules)
        [IO.File]::WriteAllText((Join-Path $project 'Source/Modes.Target.cs'), @"
using Beast.BuildTool;
public sealed class ModesTarget : TargetRules {
    public ModesTarget(ModulesInfo info) { Type = TargetType.$(if ($platform -eq 'Windows') {'Application'} else {'StaticLibrary'}); Modules.Add("Launch"); }
}
"@)
        [IO.File]::WriteAllText((Join-Path $source 'Launch.cpp'), @'
#include "Pch.h"
#include <iostream>
int Function0(); int Function1(); int Isolated(); extern "C" int CFunction();
int main() { std::cout << Function0()+Function1()+Isolated()+CFunction(); }
'@)
        for ($i=0; $i -lt 16; $i++) {
            [IO.File]::WriteAllText((Join-Path $source ("Unit{0:D2}.cpp" -f $i)), "#include `"Pch.h`"`nint Function$i() { std::array<int,2> v{VALUE, $i}; return v[0]; }`n")
        }
        [IO.File]::AppendAllText((Join-Path $source 'Unit00.cpp'), "`nstatic int localMarker = 99;`n")
        [IO.File]::WriteAllText((Join-Path $source 'Isolated.cpp'), "#ifdef PCH_ACTIVE`n#error PCH exclusion failed`n#endif`nstatic int localMarker = 3; int Isolated() {return localMarker;}`n")
        [IO.File]::WriteAllText((Join-Path $source 'Native.c'), "#if defined(__cplusplus) || defined(PCH_ACTIVE)`n#error C isolation failed`n#endif`nint CFunction(void) {return 7;}`n")
        $configPath = Join-Path $project 'Config/BuildConfig.json'
        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        $config.acceleration.CacheEnabled = $false
        if ($AndroidSdk) { $config.android.sdkRoot = $AndroidSdk; $config.android.ndkRoot = Join-Path $AndroidSdk 'ndk' }
        [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10))
        $tracePath = Join-Path $project "Intermediate/Build/$platform/Debug/Modes/BuildTrace.json"
        function Build([string]$Stage) {
            Beast @('build',$project,'--target','Modes','--platform',$platform,'--config','Debug') | Set-Content (Join-Path $parent "$Stage.log")
            Copy-Item $tracePath (Join-Path $parent "$Stage.trace.json")
        }
        function Actions { (Get-Content $tracePath -Raw | ConvertFrom-Json).actions }
        function Executed { @(Actions | Where-Object { -not $_.skipped -and -not $_.cacheHit }) }
        Build 'cold'
        Check (@(Actions | Where-Object { $_.description -like 'Creating PCH*' }).Count -eq [int]$usePch) "$platform/${mode}: PCH action count"
        if ($platform -eq 'Windows') { Check ((& (Join-Path $project 'Binaries/Windows/Debug/Modes.exe')) -eq '24') 'Initial executable output' }
        Build 'noop'; Check ((Executed).Count -eq 0) "$platform/$mode no-op rebuilt"
        $unit = Join-Path $source 'Unit00.cpp'
        [IO.File]::WriteAllText($unit, ([IO.File]::ReadAllText($unit).Replace('return v[0]', 'return v[0]+1')))
        Build 'source-edit'
        Check (@(Executed | Where-Object { $_.description -like 'Compiling*' }).Count -eq 1) "$platform/$mode source edit rebuilt more than one unit/batch"
        Check (@(Executed | Where-Object { $_.description -like 'Creating PCH*' }).Count -eq 0) 'Source edit rebuilt PCH'
        if ($platform -eq 'Windows') { Check ((& (Join-Path $project 'Binaries/Windows/Debug/Modes.exe')) -eq '25') 'Edited executable output' }
        $stamp = (Get-Item $header).LastWriteTimeUtc
        [IO.File]::WriteAllText($header, "#pragma once`n#define VALUE 8`n"); (Get-Item $header).LastWriteTimeUtc = $stamp
        Build 'header-edit'
        Check (@(Executed | Where-Object { $_.description -like 'Creating PCH*' }).Count -eq [int]$usePch) 'Nested header did not rebuild PCH'
        Check (@(Executed | Where-Object { $_.description -match 'Compiling (Native.c|Isolated.cpp)' }).Count -eq 0) 'PCH leaked into excluded/C files'
        if ($platform -eq 'Windows') { Check ((& (Join-Path $project 'Binaries/Windows/Debug/Modes.exe')) -eq '27') 'Header edit executable output' }
        (Get-Item $header).LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(1)
        Build 'touch'; Check ((Executed).Count -eq 0) 'Touch-only PCH header rebuild'
        [IO.File]::AppendAllText($unit, "`n// Recompile with touched PCH input`n")
        Build 'touch-consumer'
        if ($usePch) {
            [IO.File]::AppendAllText($header, "`n#error INTENTIONAL_PCH_FAILURE`n")
            $failure = (& $tool build $project --target Modes --platform $platform --config Debug 2>&1 | Out-String)
            Check ($LASTEXITCODE -ne 0 -and $failure.Contains('INTENTIONAL_PCH_FAILURE')) 'Broken PCH accepted'
            [IO.File]::WriteAllText($header, "#pragma once`n#define VALUE 8`n")
            Build 'repaired'
        }
        # Switching optimizations off must not link stale unity/PCH objects.
        [IO.File]::WriteAllText((Join-Path $source 'Launch.Build.cs'), $rules.Replace('"Pch.h"','""').Replace('UseUnityBuild = true','UseUnityBuild = false'))
        Build 'disabled'
        Check (@(Actions | Where-Object { $_.description -match 'Creating PCH|Compiling Unity_' }).Count -eq 0) 'Disabled modes still in graph'
        if ($platform -eq 'Windows') { Check ((& (Join-Path $project 'Binaries/Windows/Debug/Modes.exe')) -eq '27') 'Disabled modes executable output' }
        Check (@(Get-ChildItem $source -Filter 'BeastPch*').Count -eq 0) 'Generated PCH source leaked into Source'
        Write-Host "PASS: $platform/$mode"
    }
}
Write-Host "PASS: compilation modes, exclusions, C isolation, no-op, source/header edits, PCH touch, failure/recovery, disabling modes. Products: $root"
