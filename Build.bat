@echo off
rem Builds Beast.BuildTool into BuildTool\bin (Release). Usage: Build.bat
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1 || (echo .NET SDK not found. Install .NET 8 SDK or newer. & exit /b 1)
dotnet build BuildTool\Beast.BuildTool.csproj -c Release -nologo
if errorlevel 1 (echo. & echo BUILD FAILED & exit /b 1)
echo. & echo Built: %~dp0BuildTool\bin\Beast.BuildTool.exe
exit /b 0
