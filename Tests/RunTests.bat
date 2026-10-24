@echo off
rem Runs the Beast test suite and prints a summary.
rem
rem   RunTests.bat              quick:   build tool, BuildGraphTests, ModelTests, Windows integration tests
rem   RunTests.bat android      quick + Android parts (NDK, Gradle, APK signing)
rem   RunTests.bat all          android + acceleration, hardening and APK packaging tests
rem   RunTests.bat bench        executor benchmark on a synthetic project
rem   RunTests.bat <mode> "C:\path\to\Android\Sdk"   explicit SDK (default: ANDROID_HOME, then %%LOCALAPPDATA%%\Android\Sdk)
rem
rem Each script keeps its project and logs in Tests\Generated. Exit code = number of failed steps.
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0.."

set "MODE=%~1"
if "%MODE%"=="" set "MODE=quick"
if /i not "%MODE%"=="quick" if /i not "%MODE%"=="android" if /i not "%MODE%"=="all" if /i not "%MODE%"=="bench" (
    echo Unknown mode "%MODE%". Use quick, android, all or bench.
    exit /b 1
)
set "SDK=%~2"
if "%SDK%"=="" set "SDK=%ANDROID_HOME%"
if "%SDK%"=="" set "SDK=%LOCALAPPDATA%\Android\Sdk"

rem The scripts and one test fixture need PowerShell 7 (pwsh); Windows PowerShell is a fallback.
set "PS=pwsh"
where pwsh >nul 2>&1 || (
    echo pwsh ^(PowerShell 7^) not found, using Windows PowerShell. BuildSteps needs pwsh: winget install Microsoft.PowerShell
    set "PS=powershell"
)
set /a PASSED=0
set /a FAILED=0
set "FAILLIST="
set "START=%TIME%"

echo === Building Beast.BuildTool
dotnet build BuildTool\Beast.BuildTool.csproj -c Release -nologo -v q
if errorlevel 1 (echo BUILD FAILED, tests were not run. & exit /b 1)

if /i "%MODE%"=="bench" (
    call :script Benchmark -Dataset Synthetic -Units 128 -Jobs 8
    call :script OptimizationBenchmark -Dataset Synthetic -Units 64 -Repeats 1
    goto summary
)

echo.
echo === BuildGraphTests
dotnet run --project Tests\BuildGraphTests\BuildGraphTests.csproj -c Release
call :result BuildGraphTests

echo.
echo === ModelTests
dotnet run --project Tests\ModelTests\ModelTests.csproj -c Release
call :result ModelTests

if /i "%MODE%"=="quick" (
    call :script Smoke
    call :script Targets
    call :script CompilationModes
    call :script BuildSteps
    call :script Features
    goto summary
)

if not exist "%SDK%" (
    echo Android SDK not found: "%SDK%". Pass it as the second argument or set ANDROID_HOME.
    set /a FAILED+=1
    set "FAILLIST=!FAILLIST! AndroidSdk"
    goto summary
)
call :script Smoke -Android -AndroidSdk "%SDK%"
call :script Targets -Android -AndroidSdk "%SDK%"
call :script CompilationModes -Android -AndroidSdk "%SDK%"
call :script BuildSteps -Android -AndroidSdk "%SDK%"
call :script Features
if /i "%MODE%"=="all" (
    call :script Acceleration -AndroidSdk "%SDK%"
    call :script Hardening -AndroidSdk "%SDK%"
    call :script AndroidPackaging -AndroidSdk "%SDK%"
)

:summary
echo.
echo ============================================================
echo  Mode %MODE%: %PASSED% passed, %FAILED% failed   (started %START%, finished %TIME%)
if not "%FAILLIST%"=="" echo  Failed:%FAILLIST%
echo  Logs and test projects: %CD%\Tests\Generated
echo ============================================================
rem Keep the window open when started by double-click.
echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %FAILED%

rem --- call :script Name [arguments...] : runs Tests\Name.ps1 and records the result
:script
set "NAME=%~1"
shift
set "ARGS="
:collect
if "%~1"=="" goto run
set ARGS=!ARGS! %1
shift
goto collect
:run
echo.
echo === %NAME%%ARGS%
%PS% -NoProfile -ExecutionPolicy Bypass -File "Tests\%NAME%.ps1"%ARGS%
call :result %NAME%
exit /b 0

rem --- call :result Name : counts the exit code of the previous command
:result
if errorlevel 1 (
    set /a FAILED+=1
    set "FAILLIST=!FAILLIST! %~1"
    echo --- %~1: FAILED
) else (
    set /a PASSED+=1
    echo --- %~1: passed
)
exit /b 0
