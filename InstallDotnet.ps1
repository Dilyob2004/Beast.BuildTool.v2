<#
Installs the .NET 10 SDK needed to build Beast BuildTool (Windows), without administrator rights.
  .\InstallDotnet.ps1                  install into %LOCALAPPDATA%\Microsoft\dotnet (if no .NET 10+ SDK is found)
                                       and add it to the user PATH
  .\InstallDotnet.ps1 -Dir <path>      install somewhere else
  .\InstallDotnet.ps1 -Force           install even if a .NET 10+ SDK is already present
  .\InstallDotnet.ps1 -NoPath          do not change the user PATH / DOTNET_ROOT
If scripts are blocked: powershell -ExecutionPolicy Bypass -File .\InstallDotnet.ps1
Uses Microsoft's official dotnet-install.ps1 (https://dot.net/v1/dotnet-install.ps1), channel 10.0.
#>
param(
    [string]$Dir = (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'),
    [switch]$Force,
    [switch]$NoPath
)
$ErrorActionPreference = 'Stop'
$Channel = '10.0'

function Get-SdkMajor([string]$Dotnet) {
    try {
        $majors = & $Dotnet --list-sdks 2>$null | ForEach-Object { [int]($_ -split '\.')[0] }
        if ($majors) { return ($majors | Measure-Object -Maximum).Maximum }
    } catch { }
    return 0
}

if (-not $Force) {
    $candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, (Join-Path $Dir 'dotnet.exe')) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($candidate in $candidates) {
        if ((Get-SdkMajor $candidate) -ge 10) {
            Write-Host ".NET SDK $(& $candidate --version) is already installed ($candidate). Nothing to do."
            exit 0
        }
    }
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$script = Join-Path ([IO.Path]::GetTempPath()) "dotnet-install-$([Guid]::NewGuid().ToString('N')).ps1"
try {
    Write-Host 'Downloading dotnet-install.ps1...'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script
    Write-Host "Installing the .NET $Channel SDK into $Dir..."
    & $script -Channel $Channel -InstallDir $Dir -NoPath
} finally {
    Remove-Item $script -ErrorAction SilentlyContinue
}
$dotnet = Join-Path $Dir 'dotnet.exe'
Write-Host "Installed .NET SDK $(& $dotnet --version) into $Dir"

if (-not $NoPath) {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $Dir, 'User')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ })
    if ($entries -notcontains $Dir) {
        [Environment]::SetEnvironmentVariable('Path', (@($Dir) + $entries) -join ';', 'User')
        Write-Host "Added $Dir to the user PATH."
    }
    $env:DOTNET_ROOT = $Dir
    $env:Path = "$Dir;$env:Path"
    Write-Host 'Open a new terminal, then: dotnet --version'
} else {
    Write-Host "PATH was not changed. Use: `$env:Path = `"$Dir;`$env:Path`""
}
