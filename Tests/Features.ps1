# Windows integration tests for target dependencies (DLL + dllimport), LTO, run, test, install,
# package, AddressSanitizer, MASM (.asm), C++20 modules, clang-cl, ARM64 and vcpkg/Conan. Optional parts are
# skipped when their tools are missing:
#   clang-cl  - Visual Studio "C++ Clang tools for Windows"
#   ARM64     - "MSVC ARM64 build tools" (built only; runs on an ARM64 host)
#   vcpkg     - VCPKG_ROOT or the vcpkg bundled with Visual Studio (downloads fmt on first run)
#   Conan     - conan 2 on PATH with a default profile (downloads fmt on first run)
# -SkipPackages skips vcpkg and Conan (they need network access).
param([switch]$SkipPackages)
$ErrorActionPreference = 'Stop'
$tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'BuildTool/bin/Beast.BuildTool.exe'
$testRoot = Join-Path $PSScriptRoot ('Generated/Features ' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $testRoot 'Features'
$script:skipped = @()
function Beast([string[]]$Arguments) {
    $log = @(& $tool @Arguments 2>&1 | ForEach-Object { "$_" })
    $log | Write-Host
    if ($LASTEXITCODE -ne 0) {
        $tail = ($log | Where-Object { $_ -notmatch ' Verbose ' } | Select-Object -Last 40) -join "`n"
        throw "Beast failed: $Arguments`n$tail"
    }
}
# In GitHub Actions the failure is also reported as an annotation (readable without the job log).
trap {
    if ($env:GITHUB_ACTIONS) { Write-Host ("::error title=Features.ps1::" + ("$($_.Exception.Message)`n$($_.InvocationInfo.PositionMessage)" -replace '%', '%25' -replace "`r", '' -replace "`n", '%0A')) }
    break
}
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Skip([string]$Message) { Write-Host "SKIP: $Message"; $script:skipped += $Message }
function Put([string]$Path, [string]$Text) {
    $full = Join-Path $project $Path
    New-Item -ItemType Directory -Force -Path (Split-Path $full -Parent) | Out-Null
    [IO.File]::WriteAllText($full, $Text)
}
function Module([string]$Name, [string]$Body) {
    Put "Source/$Name/$Name.Build.cs" "using Beast.BuildTool;`npublic sealed class $Name : ModuleRules { public $Name(ModulesInfo info) { $Body } }`n"
}
function Target([string]$Name, [string]$Body) {
    Put "Source/$Name.Target.cs" "using Beast.BuildTool;`npublic sealed class ${Name}Target : TargetRules { public ${Name}Target(ModulesInfo info) { $Body } }`n"
}
function Binaries([string]$Configuration, [string]$Platform = 'Windows') { Join-Path $project "Binaries/$Platform/$Configuration" }
function Output([string]$Exe) {
    $text = (& $Exe | Out-String).Trim()
    Check ($LASTEXITCODE -eq 0) "$Exe exited with $LASTEXITCODE"
    return $text
}
function SetConfig([scriptblock]$Change) {
    $path = Join-Path $project 'Config/BuildConfig.json'
    $config = Get-Content $path -Raw | ConvertFrom-Json
    & $Change $config
    [IO.File]::WriteAllText($path, ($config | ConvertTo-Json -Depth 10))
}

Beast @('new', 'Features', '--path', $testRoot)
$source = [IO.Path]::GetFullPath((Join-Path $project 'Source'))
Check ($source.StartsWith([IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar)) 'Unsafe test path'
Remove-Item -LiteralPath $source -Recurse -Force

# Engine.dll exports CoreAnswer (CORE_API = dllexport there, dllimport in Game and CoreTests).
Module 'Core' 'PublicIncludePaths.Add("Public");'
Put 'Source/Core/Public/Core.h' "#pragma once`nCORE_API int CoreAnswer();`n"
Put 'Source/Core/Core.cpp' "#include `"Core.h`"`nint CoreAnswer() { return 42; }`n"
Module 'Launch' 'PrivateDependencies.Add("Core");'
Put 'Source/Launch/Main.cpp' "#include <cstdio>`n#include `"Core.h`"`nint main() { std::printf(`"%d\n`", CoreAnswer()); return 0; }`n"
Target 'Engine' 'Type = TargetType.SharedLibrary; Modules.Add("Core");'
Target 'Game' 'Modules.Add("Launch"); TargetDependencies.Add("Engine"); LinkTimeOptimization = true;'
# Tests: CoreTests passes only with its TestArguments; FailingTests exits with 3.
Module 'CoreTestsMain' 'PrivateDependencies.Add("Core");'
Put 'Source/CoreTestsMain/Tests.cpp' "#include <cstring>`n#include `"Core.h`"`nint main(int argc, char** argv) { return CoreAnswer() == 42 && argc == 2 && std::strcmp(argv[1], `"--ok`") == 0 ? 0 : 1; }`n"
Target 'CoreTests' 'Modules.Add("CoreTestsMain"); TargetDependencies.Add("Engine"); IsTest = true; TestArguments.Add("--ok");'
Module 'FailingMain' ''
Put 'Source/FailingMain/Failing.cpp' "int main() { return 3; }`n"
Target 'FailingTests' 'Modules.Add("FailingMain"); IsTest = true;'
# AddressSanitizer: a heap allocation must run cleanly with the ASan runtime next to the EXE.
Module 'AsanMain' ''
Put 'Source/AsanMain/Asan.cpp' "#include <cstdio>`n#include <vector>`nint main() { std::vector<int> v(16, 1); int s = 0; for (int x : v) s += x; std::printf(`"asan %d\n`", s); return 0; }`n"
Target 'Asan' 'Modules.Add("AsanMain"); Sanitizers = Sanitizer.Address;'
# MASM: ml64 assembles Two.asm (x64 only).
Module 'AsmMain' ''
Put 'Source/AsmMain/Two.asm' ".code`nAsmTwo PROC`n    mov eax, 2`n    ret`nAsmTwo ENDP`nEND`n"
Put 'Source/AsmMain/Main.cpp' "#include <cstdio>`nextern `"C`" int AsmTwo();`nint main() { std::printf(`"%d\n`", 40 + AsmTwo()); return 0; }`n"
Target 'AsmDemo' 'Modules.Add("AsmMain");'
# C++20 modules (MSVC /interface, /internalPartition, /reference): a static library target exports
# module Geo (interface + partition interface + internal partition + implementation unit).
Module 'Geo' ''
Put 'Source/Geo/Geo.ixx' "export module Geo;`nexport import :Shapes;`nimport :Detail;`nexport int Area(int w, int h);`nexport int Scaled(int v) { return v * Factor(); }`n"
Put 'Source/Geo/Shapes.ixx' "export module Geo:Shapes;`nexport struct Rect { int w, h; };`n"
Put 'Source/Geo/Detail.ixx' "module Geo:Detail;`nint Factor() { return 2; }`n"
Put 'Source/Geo/Area.cpp' "module Geo;`nint Area(int w, int h) { Rect r{w, h}; return r.w * r.h; }`n"
Target 'GeoLib' 'Type = TargetType.StaticLibrary; Modules.Add("Geo");'
Module 'ModMain' 'PrivateDependencies.Add("Geo");'
Put 'Source/ModMain/Main.cpp' "#include <cstdio>`nimport Geo;`nint main() { std::printf(`"%d\n`", Area(3, 7) * Scaled(1)); return 0; }`n"
Target 'ModDemo' 'Modules.Add("ModMain"); TargetDependencies.Add("GeoLib");'

# Target dependencies: building Game builds Engine first; Game.exe imports from Engine.dll.
foreach ($configuration in @('Debug', 'Release')) {
    Beast @('build', $project, '--target', 'Game', '--config', $configuration)
    $bin = Binaries $configuration
    Check (Test-Path (Join-Path $bin 'Engine.dll')) "Engine.dll missing ($configuration)"
    Check (Test-Path (Join-Path $bin 'Engine.lib')) "Engine import library missing ($configuration)"
    Check (Test-Path (Join-Path $bin 'Game.pdb')) "PDB missing ($configuration)"
    Check ((Output (Join-Path $bin 'Game.exe')) -eq '42') "Wrong Game output ($configuration)"
}
$commands = Get-Content (Join-Path $project 'compile_commands.json') -Raw
Check ($commands.Contains('/GL')) 'LinkTimeOptimization: /GL missing from Release compile commands'
Check ($commands.Contains('CORE_API=__declspec(dllimport)')) 'CORE_API is not dllimport in Game'
Write-Host 'PASS: target dependencies, dllimport, LTO'

# run --platform Windows starts the EXE; its exit code is Beast's.
& $tool run $project --platform Windows --target Game --config Debug --no-build
Check ($LASTEXITCODE -eq 0) 'run --platform Windows failed'
& $tool build $project --target Game --config Debug --run
Check ($LASTEXITCODE -eq 0) 'build --run failed'
Write-Host 'PASS: run'

# test: CoreTests receives TestArguments; a failing test fails the command.
Beast @('test', $project, '--target', 'CoreTests')
$log = (& $tool test $project --target 'CoreTests,FailingTests' 2>&1 | Out-String)
Check ($LASTEXITCODE -ne 0) 'Failing test target reported success'
Check ($log.Contains('FailingTests')) 'Failing test not reported'
Write-Host 'PASS: test'

# install / package: Release by default.
$prefix = Join-Path $testRoot 'Install'
Beast @('install', $project, '--prefix', $prefix, '--target', 'Game,Engine', '--symbols')
foreach ($file in @('bin/Game.exe', 'bin/Engine.dll', 'bin/Game.pdb', 'lib/Engine.lib', 'include/Core.h')) {
    Check (Test-Path (Join-Path $prefix $file)) "install: $file missing"
}
Check ((Output (Join-Path $prefix 'bin/Game.exe')) -eq '42') 'Installed Game does not run'
Beast @('package', $project, '--target', 'Game,Engine')
$zip = Join-Path $project 'Binaries/Packages/Features-Windows-Release.zip'
Check (Test-Path $zip) 'package: zip missing'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try { $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }) } finally { $archive.Dispose() }
Check ($entries -contains 'bin/Game.exe' -and $entries -contains 'bin/Engine.dll') 'package: zip content'
Write-Host 'PASS: install, package'

# The installed Engine is usable from CMake: find_package(Engine) + Engine::Engine (dllimport, import library).
if (Get-Command cmake -ErrorAction SilentlyContinue) {
    $consumer = Join-Path $testRoot 'Consumer'
    New-Item -ItemType Directory -Force -Path $consumer | Out-Null
    [IO.File]::WriteAllText((Join-Path $consumer 'main.cpp'), "#include <cstdio>`n#include `"Core.h`"`nint main() { std::printf(`"%d\n`", CoreAnswer()); return 0; }`n")
    [IO.File]::WriteAllText((Join-Path $consumer 'CMakeLists.txt'), "cmake_minimum_required(VERSION 3.16)`nproject(Consumer CXX)`nfind_package(Engine CONFIG REQUIRED)`nadd_executable(consumer main.cpp)`ntarget_link_libraries(consumer PRIVATE Engine::Engine)`n")
    & cmake -S $consumer -B (Join-Path $consumer 'build') "-DCMAKE_PREFIX_PATH=$prefix"
    Check ($LASTEXITCODE -eq 0) 'CMake consumer configure failed'
    & cmake --build (Join-Path $consumer 'build') --config Release
    Check ($LASTEXITCODE -eq 0) 'CMake consumer build failed'
    $env:PATH = (Join-Path $prefix 'bin') + ';' + $env:PATH
    Check ((Output (Join-Path $consumer 'build/Release/consumer.exe')) -eq '42') 'CMake consumer output'
    Write-Host 'PASS: CMake package'
} else { Skip 'CMake consumer: cmake is not on PATH' }

# AddressSanitizer (MSVC): the runtime DLL is copied next to the EXE.
Beast @('build', $project, '--target', 'Asan', '--config', 'Debug')
$bin = Binaries 'Debug'
Check (@(Get-ChildItem $bin -Filter 'clang_rt.asan*.dll').Count -gt 0) 'ASan runtime DLL not copied'
Check ((Output (Join-Path $bin 'Asan.exe')) -eq 'asan 16') 'ASan build does not run'
Write-Host 'PASS: AddressSanitizer (MSVC)'

Beast @('build', $project, '--target', 'AsmDemo', '--config', 'Debug')
Check ((Output (Join-Path $bin 'AsmDemo.exe')) -eq '42') 'MASM object not linked'
Write-Host 'PASS: MASM'

Beast @('build', $project, '--target', 'ModDemo', '--config', 'Debug')
Check ((Output (Join-Path $bin 'ModDemo.exe')) -eq '42') 'C++20 module program output'
Write-Host 'PASS: C++20 modules (MSVC)'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = if (Test-Path $vswhere) { (& $vswhere -latest -products * -property installationPath | Select-Object -First 1) } else { $null }

# ARM64: built (and run only on an ARM64 host).
$arm64 = $vs -and @(Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC/*/bin/Hostx64/arm64/cl.exe') -ErrorAction SilentlyContinue).Count -gt 0
if ($arm64) {
    Beast @('build', $project, '--target', 'Game', '--arch', 'arm64', '--config', 'Debug')
    $armBin = Binaries 'Debug' 'WindowsArm64'
    Check ((Test-Path (Join-Path $armBin 'Game.exe')) -and (Test-Path (Join-Path $armBin 'Engine.dll'))) 'ARM64 products missing'
    $stream = [IO.File]::OpenRead((Join-Path $armBin 'Game.exe'))
    try {
        $reader = New-Object IO.BinaryReader($stream)
        $stream.Position = 0x3C; $stream.Position = $reader.ReadInt32() + 4
        Check ($reader.ReadUInt16() -eq 0xAA64) 'Game.exe is not an ARM64 image'
    } finally { $stream.Dispose() }
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { Check ((Output (Join-Path $armBin 'Game.exe')) -eq '42') 'ARM64 Game output' }
    Write-Host 'PASS: Windows ARM64'
} else { Skip 'ARM64: MSVC ARM64 build tools are not installed' }

# clang-cl: same project, compiler switched in Config/BuildConfig.json.
$clang = $vs -and (Test-Path (Join-Path $vs 'VC/Tools/Llvm/x64/bin/clang-cl.exe'))
if ($clang) {
    SetConfig { param($c) $c.windows | Add-Member -NotePropertyName compiler -NotePropertyValue 'clang-cl' -Force }
    try {
        Beast @('build', $project, '--target', 'Game', '--config', 'Release')
        Check ((Output (Join-Path (Binaries 'Release') 'Game.exe')) -eq '42') 'clang-cl Game output'
        Check ((Get-Content (Join-Path $project 'compile_commands.json') -Raw).Contains('clang-cl')) 'clang-cl not used'
        Beast @('build', $project, '--target', 'Asan', '--config', 'Debug')
        Check ((Output (Join-Path (Binaries 'Debug') 'Asan.exe')) -eq 'asan 16') 'clang-cl ASan build does not run'
        Write-Host 'PASS: clang-cl (+ ASan)'
    } finally { SetConfig { param($c) $c.windows.compiler = 'msvc' } }
} else { Skip 'clang-cl: C++ Clang tools for Windows are not installed' }

# Package managers: a separate project each, so their manifests do not affect the tests above.
function PackageProject([string]$Name) {
    $script:project = Join-Path $testRoot $Name
    Beast @('new', $Name, '--path', $testRoot)
    Remove-Item -LiteralPath (Join-Path $script:project 'Source') -Recurse -Force
    Put "Source/App/Main.cpp" "#include <fmt/core.h>`nint main() { fmt::print(`"{}\n`", 6 * 7); return 0; }`n"
    Target $Name 'Modules.Add("App");'
}
if ($SkipPackages) { Skip 'vcpkg/Conan: -SkipPackages' }
else {
    $vcpkgRoot = if ($env:VCPKG_ROOT) { $env:VCPKG_ROOT } elseif ($vs) { Join-Path $vs 'VC/vcpkg' } else { '' }
    if ($vcpkgRoot -and (Test-Path (Join-Path $vcpkgRoot 'vcpkg.exe'))) {
        PackageProject 'VcpkgDemo'
        Module 'App' 'VcpkgLibraries.Add("fmt");'
        Put 'vcpkg.json' '{ "name": "vcpkgdemo", "version": "1.0.0", "dependencies": [ "fmt" ] }'
        Beast @('build', $project, '--target', 'VcpkgDemo', '--config', 'Debug')
        Check ((Output (Join-Path (Binaries 'Debug') 'VcpkgDemo.exe')) -eq '42') 'vcpkg fmt program output'
        Write-Host 'PASS: vcpkg'
    } else { Skip 'vcpkg: not found (set VCPKG_ROOT)' }
    if (Get-Command conan -ErrorAction SilentlyContinue) {
        PackageProject 'ConanDemo'
        Module 'App' ''
        Put 'conanfile.txt' "[requires]`nfmt/10.2.1`n"
        Beast @('build', $project, '--target', 'ConanDemo', '--config', 'Release')
        Check ((Output (Join-Path (Binaries 'Release') 'ConanDemo.exe')) -eq '42') 'Conan fmt program output'
        Write-Host 'PASS: Conan'
    } else { Skip 'Conan: conan is not on PATH' }
}

Write-Host "PASS: feature integration tests. Skipped: $($script:skipped.Count). Products: $testRoot"
