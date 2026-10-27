@echo off
rem Builds Beast.BuildTool into BuildTool\bin (Release). Usage: Build.bat
rem Needs the .NET 10 SDK (or newer): InstallDotnet.ps1 installs it.
setlocal EnableDelayedExpansion
cd /d "%~dp0"
set "DOTNET="
for %%d in ("%DOTNET_ROOT%\dotnet.exe" "dotnet" "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" "%ProgramFiles%\dotnet\dotnet.exe") do (
    if not defined DOTNET (
        set BEST=0
        for /f "tokens=1 delims=." %%v in ('"%%~d" --list-sdks 2^>nul') do if %%v GTR !BEST! set BEST=%%v
        if !BEST! GEQ 10 set "DOTNET=%%~d"
    )
)
if not defined DOTNET (
    echo .NET 10 SDK not found. Install it with: powershell -ExecutionPolicy Bypass -File InstallDotnet.ps1
    exit /b 1
)
"%DOTNET%" build BuildTool\Beast.BuildTool.csproj -c Release -nologo
if errorlevel 1 (echo. & echo BUILD FAILED & exit /b 1)
echo. & echo Built: %~dp0BuildTool\bin\Beast.BuildTool.exe
exit /b 0
