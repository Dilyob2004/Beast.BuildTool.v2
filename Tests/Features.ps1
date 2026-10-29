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
    if ($env:GITHUB_ACTIONS) {
        $text = "$($_.Exception.Message)`n$($_.InvocationInfo.PositionMessage)"
        if ($text.Length -gt 4000) { $text = $text.Substring(0, 4000) + '...' }
        Write-Host ("::error title=Features.ps1::" + ($text -replace '%', '%25' -replace "`r", '' -replace "`n", '%0A'))
    }
    break
}
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
# Progress is also an annotation: a failure without an exception (a native exit code) shows how far the script got.
function Pass([string]$Message) { Write-Host "PASS: $Message"; if ($env:GITHUB_ACTIONS) { Write-Host "::notice title=Features.ps1::$Message" } }
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
Put 'Source/Geo/Detail.cpp' "module Geo:Detail;`nint Factor() { return 2; }`n"
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
Pass 'target dependencies, dllimport, LTO'

# run --platform Windows starts the EXE; its exit code is Beast's.
& $tool run $project --platform Windows --target Game --config Debug --no-build
Check ($LASTEXITCODE -eq 0) 'run --platform Windows failed'
& $tool build $project --target Game --config Debug --run
Check ($LASTEXITCODE -eq 0) 'build --run failed'
Pass 'run'

# test: CoreTests receives TestArguments; a failing test fails the command.
Beast @('test', $project, '--target', 'CoreTests')
$log = (& $tool test $project --target 'CoreTests,FailingTests' 2>&1 | Out-String)
Check ($LASTEXITCODE -ne 0) 'Failing test target reported success'
Check ($log.Contains('FailingTests')) 'Failing test not reported'
Pass 'test'

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
Pass 'install, package'

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
    Pass 'CMake package'
} else { Skip 'CMake consumer: cmake is not on PATH' }

# AddressSanitizer (MSVC): the runtime DLL is copied next to the EXE.
Beast @('build', $project, '--target', 'Asan', '--config', 'Debug')
$bin = Binaries 'Debug'
Check (@(Get-ChildItem $bin -Filter 'clang_rt.asan*.dll').Count -gt 0) 'ASan runtime DLL not copied'
Check ((Output (Join-Path $bin 'Asan.exe')) -eq 'asan 16') 'ASan build does not run'
Pass 'AddressSanitizer (MSVC)'

Beast @('build', $project, '--target', 'AsmDemo', '--config', 'Debug')
Check ((Output (Join-Path $bin 'AsmDemo.exe')) -eq '42') 'MASM object not linked'
Pass 'MASM'

Beast @('build', $project, '--target', 'ModDemo', '--config', 'Debug')
Check ((Output (Join-Path $bin 'ModDemo.exe')) -eq '42') 'C++20 module program output'
Pass 'C++20 modules (MSVC)'

# import std (MSVC modules/std.ixx): a module exported by a static library uses std; the program
# imports std and that module; std is built once, by the library target.
Module 'StdText' ''
Put 'Source/StdText/StdText.ixx' "export module StdText;`nimport std;`nexport std::string Greeting(std::string_view name) { return std::format(`"Hello, {}!`", name); }`n"
Target 'StdTextLib' 'Type = TargetType.StaticLibrary; Modules.Add("StdText"); CppVersion = CppVersion.Latest;'
Module 'StdMain' 'PrivateDependencies.Add("StdText");'
Put 'Source/StdMain/Main.cpp' "import std;`nimport StdText;`nint main() { std::cout << Greeting(`"std`") << '\n'; }`n"
Target 'StdDemo' 'Modules.Add("StdMain"); TargetDependencies.Add("StdTextLib"); CppVersion = CppVersion.Latest;'
Beast @('build', $project, '--target', 'StdDemo', '--config', 'Debug')
Check ((Output (Join-Path $bin 'StdDemo.exe')) -eq 'Hello, std!') 'import std program output'
Pass 'import std (MSVC)'

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
    Pass 'Windows ARM64'
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
        Pass 'clang-cl (+ ASan)'
    } finally { SetConfig { param($c) $c.windows.compiler = 'msvc' } }
} else { Skip 'clang-cl: C++ Clang tools for Windows are not installed' }

# Resources: copied next to the EXE (with a dependency target's resources), read through
# BeastResources.h, including a non-ASCII file name (UTF-8 -> UTF-16 on Windows).
Module 'ResData' 'Resources.Add("Data", "Assets/Data");'
Put 'Source/ResData/Data/level.txt' "level 7"
Put 'Source/ResData/Stub.cpp' "int ResDataStub() { return 7; }`n"
Module 'ResApp' 'Resources.Add("Assets");'
Put 'Source/ResApp/Assets/greeting.txt' "resource 42"
Put "Source/ResApp/Assets/$([char]0x043F)$([char]0x0440)$([char]0x0438)$([char]0x0432)$([char]0x0435)$([char]0x0442).txt" "unicode"
Put 'Source/ResApp/Main.cpp' @'
#include "BeastResources.h"
#include <cstdio>
#include <string>
static std::string Text(const char* path) { std::string text; return Beast::ReadResource(path, text) ? text : "missing"; }
int main() { std::printf("%s|%s|%s\n", Text("Assets/greeting.txt").c_str(), Text("Assets/Data/level.txt").c_str(), Text("Assets/\xd0\xbf\xd1\x80\xd0\xb8\xd0\xb2\xd0\xb5\xd1\x82.txt").c_str()); return 0; }
'@
Target 'ResDataLib' 'Type = TargetType.StaticLibrary; Modules.Add("ResData");'
Target 'Res' 'Modules.Add("ResApp"); TargetDependencies.Add("ResDataLib");'
Beast @('build', $project, '--target', 'Res', '--config', 'Debug')
Check (Test-Path (Join-Path (Binaries 'Debug') 'Assets/Data/level.txt')) 'resources of the dependency target not copied'
Check ((Output (Join-Path (Binaries 'Debug') 'Res.exe')) -eq 'resource 42|level 7|unicode') 'resources not read by BeastResources.h'
Pass 'resources (next to the EXE, dependency target, non-ASCII name)'

# External projects (Tests/Fixtures/External): AppA and AppB link the SDK project's static library
# target; both build at the same time, the SDK tree stays clean, an SDK header edit rebuilds both
# sides, and the Visual Studio solution contains the SDK target and its sources.
$ext = Join-Path $testRoot 'External'
Copy-Item -Recurse (Join-Path $PSScriptRoot 'Fixtures/External') $ext
$jobs = foreach ($app in 'AppA', 'AppB') {
    $process = Start-Process -FilePath $tool -ArgumentList @('build', "`"$(Join-Path $ext $app)`"", '--config', 'Debug') -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $testRoot "$app.log") -RedirectStandardError (Join-Path $testRoot "$app.err")
    $null = $process.Handle   # keeps the handle, so ExitCode is available after the process ends
    [pscustomobject]@{ App = $app; Process = $process }
}
foreach ($job in $jobs) {
    $job.Process.WaitForExit()
    if ($job.Process.ExitCode -ne 0) {
        $tail = (Get-Content (Join-Path $testRoot "$($job.App).log") | Where-Object { $_ -notmatch ' Verbose ' } | Select-Object -Last 30) -join "`n"
        throw "external project: $($job.App) build failed (exit $($job.Process.ExitCode))`n$tail"
    }
}
foreach ($app in 'AppA', 'AppB') {
    $output = Output (Join-Path $ext "$app/Binaries/Windows/Debug/$app.exe")
    Check ($output -eq "$app 42 1 1 resource") "$app output: $output"
    Check (Test-Path (Join-Path $ext "$app/Intermediate/Build/Windows/Debug/External/SDK/SDK")) "${app}: SDK not built under External/SDK"
}
Check (-not (Test-Path (Join-Path $ext 'SDK/Intermediate')) -and -not (Test-Path (Join-Path $ext 'SDK/Binaries'))) 'build files were written into the external project'
$header = Join-Path $ext 'SDK/Source/Core/Public/SdkCore.h'
[IO.File]::WriteAllText($header, ([IO.File]::ReadAllText($header) -replace 'SDK_VERSION 1', 'SDK_VERSION 2'))
Beast @('build', (Join-Path $ext 'AppA'), '--config', 'Debug')
Check ((Output (Join-Path $ext 'AppA/Binaries/Windows/Debug/AppA.exe')) -eq 'AppA 43 2 1 resource') 'AppA not rebuilt after the SDK header edit'
Beast @('generate', (Join-Path $ext 'AppA'))
Check (Test-Path (Join-Path $ext 'AppA/SDK.vcxproj')) 'no SDK.vcxproj in the solution'
Check ((Get-Content (Join-Path $ext 'AppA/AppA.vcxproj.filters') -Raw) -match 'External\\SDK\\Source\\Widgets') 'SDK sources missing from the filters'
Check ((Get-Content (Join-Path $ext 'AppA/AppA.sln') -Raw) -match 'SDK.vcxproj') 'SDK.vcxproj missing from the solution'
Pass 'external projects (parallel consumers, clean external tree, header edit, Visual Studio)'

# Custom template: placeholders, binary file, externalProjects via {{TemplateDirRelative}}, Visual Studio files.
$fixtures = Join-Path $testRoot 'Fixtures'
New-Item -ItemType Directory -Force $fixtures | Out-Null
Copy-Item -Recurse (Join-Path $PSScriptRoot 'Fixtures/External'), (Join-Path $PSScriptRoot 'Fixtures/Templates') $fixtures
Beast @('new', 'MyUi', '--path', (Join-Path $testRoot 'Projects'), '--template', (Join-Path $fixtures 'Templates/UIApplication'))
$ui = Join-Path $testRoot 'Projects/MyUi'
Check (Test-Path (Join-Path $ui 'Source/MyUi.Target.cs')) 'template file name placeholder'
Check (Test-Path (Join-Path $ui 'MyUi.sln')) 'solution not generated for the template project'
$logo = Join-Path $ui 'Source/MyUiApp/Assets/logo.bin'
Check ((Get-FileHash $logo).Hash -eq (Get-FileHash -LiteralPath (Join-Path $fixtures 'Templates/UIApplication/Source/{{ProjectIdentifier}}App/Assets/logo.bin')).Hash) 'binary template file changed'
Beast @('build', $ui, '--config', 'Debug')
Check ((Output (Join-Path $ui 'Binaries/Windows/Debug/MyUi.exe')) -eq 'MyUi template 42 22') 'project from the template'
Write-Host 'PASS: custom templates (new --template)'

# watch: initial build, rebuild after a change, numbered DLL copies (--versioned), --on-success.
$watchProject = Join-Path $testRoot 'Watch'
$savedProject = $project; $project = $watchProject
Beast @('new', 'Watch', '--path', $testRoot)
Remove-Item -Recurse -Force (Join-Path $watchProject 'Source')
Module 'WatchLib' 'PublicIncludePaths.Add("Public");'
Put 'Source/WatchLib/Public/WatchLib.h' "#pragma once`nWATCHLIB_API int WatchValue();`n"
Put 'Source/WatchLib/WatchLib.cpp' "#include `"WatchLib.h`"`nint WatchValue() { return 1; }`n"
Module 'WatchApp' 'PrivateDependencies.Add("WatchLib");'
Put 'Source/WatchApp/Main.cpp' "#include `"WatchLib.h`"`n#include <cstdio>`nint main() { std::printf(`"watch %d\n`", WatchValue()); return 0; }`n"
Target 'WatchLib' 'Type = TargetType.SharedLibrary; Modules.Add("WatchLib");'
Target 'Watch' 'Modules.Add("WatchApp"); TargetDependencies.Add("WatchLib");'
$hooks = Join-Path $testRoot 'watch-hooks.txt'
$watch = Start-Process -FilePath $tool -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $testRoot 'watch.log') -RedirectStandardError (Join-Path $testRoot 'watch.err') `
    -ArgumentList @('watch', "`"$watchProject`"", '--target', 'Watch', '--config', 'Debug', '--versioned', '--debounce', '200', '--on-success', "`"echo %BEAST_BUILD% %BEAST_TARGET%>>..\watch-hooks.txt`"")   # runs in the project directory
function WaitFor([scriptblock]$Condition, [string]$Message) {
    for ($i = 0; $i -lt 240; $i++) { if (& $Condition) { return }; Start-Sleep -Milliseconds 500 }
    throw "$Message`n$(Get-Content (Join-Path $testRoot 'watch.log') -Raw)"
}
try {
    WaitFor { (Test-Path $hooks) -and ((Get-Content $hooks) -match '^1 Watch') } 'watch: initial build or --on-success'
    $hot = Join-Path (Binaries 'Debug') 'Hot'
    Check (Test-Path (Join-Path $hot 'WatchLib.1.dll')) 'watch --versioned: WatchLib.1.dll'
    Start-Sleep -Seconds 1
    Put 'Source/WatchLib/WatchLib.cpp' "#include `"WatchLib.h`"`nint WatchValue() { return 2; }`n"
    WaitFor { (Get-Content $hooks) -match '^2 Watch' } 'watch: no rebuild after a source change'
    Check ((Output (Join-Path (Binaries 'Debug') 'Watch.exe')) -eq 'watch 2') 'watch: rebuilt program'
    Check (Test-Path (Join-Path $hot 'WatchLib.2.dll')) 'watch --versioned: WatchLib.2.dll'
} finally {
    if (-not $watch.HasExited) { Stop-Process -Id $watch.Id -Force }
    $project = $savedProject
}
Pass 'watch (rebuild, --versioned, --on-success)'

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
        Pass 'vcpkg'
    } else { Skip 'vcpkg: not found (set VCPKG_ROOT)' }
    if (Get-Command conan -ErrorAction SilentlyContinue) {
        PackageProject 'ConanDemo'
        Module 'App' ''
        Put 'conanfile.txt' "[requires]`nfmt/10.2.1`n"
        Beast @('build', $project, '--target', 'ConanDemo', '--config', 'Release')
        Check ((Output (Join-Path (Binaries 'Release') 'ConanDemo.exe')) -eq '42') 'Conan fmt program output'
        Pass 'Conan'
    } else { Skip 'Conan: conan is not on PATH' }
}

Pass "feature integration tests. Skipped: $($script:skipped.Count). Products: $testRoot"
# Every check above throws on failure; a leftover $LASTEXITCODE of an expected failing command must not fail the script.
exit 0
