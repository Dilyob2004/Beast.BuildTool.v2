<#
Installs the Android toolchain Beast needs (Windows), without administrator rights:
  JDK 17 (Eclipse Temurin), Android SDK command-line tools, platform-tools, platforms;android-35,
  build-tools;35.0.0 and the NDK; optionally the emulator, the x86_64 system image and AVD beast_api34.

  .\InstallAndroid.ps1                 SDK in the Android Studio location (%LOCALAPPDATA%\Android\Sdk)
  .\InstallAndroid.ps1 -Dir <path>     SDK somewhere else
  .\InstallAndroid.ps1 -JdkDir <path>  JDK 17 somewhere else (default %LOCALAPPDATA%\Beast\jdk-17)
  .\InstallAndroid.ps1 -Ndk <version>  NDK version (default: the one Beast's CI uses)
  .\InstallAndroid.ps1 -CmdlineTools <v>  command-line tools version (default 19.0: its sdkmanager works on its own;
                                       23.0+ hands over to "Android CLI", which collects usage metrics)
  .\InstallAndroid.ps1 -Emulator       also the emulator, a system image and AVD beast_api34
  .\InstallAndroid.ps1 -Force          download again what is already installed (including the JDK)
  .\InstallAndroid.ps1 -NoProfile      do not change the user environment (ANDROID_HOME, JAVA_HOME, PATH)
If scripts are blocked: powershell -ExecutionPolicy Bypass -File .\InstallAndroid.ps1
Licenses are accepted automatically (sdkmanager --licenses, where still needed). Sources: dl.google.com (repository
catalogue, SHA-1 checked) and api.adoptium.net (Temurin, SHA-256 checked).
#>
param(
    [string]$Dir = $(if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }),
    [string]$JdkDir = (Join-Path $env:LOCALAPPDATA 'Beast\jdk-17'),
    [string]$Ndk = '27.3.13750724',
    # sdkmanager of cmdline-tools 23.0+ is deprecated in favour of "Android CLI": it downloads that tool and
    # enables Google usage metrics. 19.0 is the pinned default (archives and SHA-1 from Google's catalogue).
    [string]$CmdlineTools = '19.0',
    [switch]$Emulator,
    [switch]$Force,
    [switch]$NoProfile
)
$ErrorActionPreference = 'Stop'
# In GitHub Actions a failure is also an annotation: the message and the failing line.
trap {
    if ($env:GITHUB_ACTIONS) {
        $text = "$($_.Exception.Message)`n$($_.InvocationInfo.PositionMessage)"
        Write-Host ("::error title=InstallAndroid.ps1::" + ($text -replace '%', '%25' -replace "`r", '' -replace "`n", '%0A'))
    }
    break
}
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with a progress bar
$Platform = 'android-35'
$BuildTools = '35.0.0'
$Avd = 'beast_api34'
$Repository = 'https://dl.google.com/android/repository'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$arch = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { 'aarch64' } else { 'x64' }
New-Item -ItemType Directory -Force $Dir | Out-Null
$Dir = (Resolve-Path $Dir).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("beast-android-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null

function Download([string]$Url, [string]$File) {
    Write-Host "Downloading $Url"
    # Up to 3 attempts, 5 then 10 seconds apart (network or DNS failures).
    for ($attempt = 1; ; $attempt++) {
        try { Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $File; return }
        catch {
            if ($attempt -ge 3) { throw "Download failed: $Url ($($_.Exception.Message)). The cause is most likely the network: check the connection to $(([Uri]$Url).Host) (DNS, proxy, firewall)." }
            Write-Host "Download failed ($($_.Exception.Message)); retrying in $($attempt * 5) s..."
            Start-Sleep -Seconds ($attempt * 5)
        }
    }
}
function JavaMajor([string]$Root) {
    $java = Join-Path $Root 'bin\java.exe'
    if (-not (Test-Path $java)) { return 0 }
    # java -version writes to stderr; Windows PowerShell 5.1 would turn that into an error under 'Stop'.
    $ErrorActionPreference = 'Continue'
    $text = (& $java -version 2>&1 | Out-String)
    if ($text -match 'version "(1\.)?(\d+)') { return [int]$Matches[2] }
    return 0
}

try {
    # --- JDK 17+ -------------------------------------------------------------------------------
    $jdk = $null
    if (-not $Force) {
        $candidates = @($env:JAVA_HOME, $JdkDir, (Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Android Studio\jbr')) | Where-Object { $_ }
        foreach ($candidate in $candidates) {
            $major = JavaMajor $candidate
            if ($major -ge 17) { $jdk = $candidate; Write-Host "JDK $major found: $jdk"; break }
        }
    }
    if (-not $jdk) {
        Write-Host "Downloading Eclipse Temurin JDK 17 (windows $arch)..."
        $info = Invoke-RestMethod -UseBasicParsing "https://api.adoptium.net/v3/assets/latest/17/hotspot?os=windows&architecture=$arch&image_type=jdk&vendor=eclipse"
        $package = @($info)[0].binary.package
        $archive = Join-Path $work 'jdk.zip'
        Download $package.link $archive
        if ((Get-FileHash -Algorithm SHA256 $archive).Hash -ne $package.checksum.ToUpperInvariant()) { throw "JDK checksum mismatch ($($package.link))" }
        Expand-Archive $archive (Join-Path $work 'jdk')
        if (Test-Path $JdkDir) { Remove-Item -Recurse -Force $JdkDir }
        New-Item -ItemType Directory -Force (Split-Path $JdkDir -Parent) | Out-Null
        Move-Item (Get-ChildItem (Join-Path $work 'jdk') -Directory | Select-Object -First 1).FullName $JdkDir
        $jdk = $JdkDir
        Write-Host "JDK $(JavaMajor $jdk) installed: $jdk"
    }
    $env:JAVA_HOME = $jdk

    # --- Command-line tools (sdkmanager) --------------------------------------------------------
    # Installed as cmdline-tools\<version> (not latest\), so an Android Studio cmdline-tools\latest of
    # another version is neither used nor replaced.
    $toolsDir = Join-Path $Dir "cmdline-tools\$CmdlineTools"
    $sdkmanager = Join-Path $toolsDir 'bin\sdkmanager.bat'
    $toolsMajor = 0; [void][int]::TryParse(($CmdlineTools -split '\.')[0], [ref]$toolsMajor)
    if ($toolsMajor -ge 23) { Write-Host "Warning: cmdline-tools ${CmdlineTools}: its sdkmanager hands over to Android CLI, which collects usage metrics." }
    if ($Force -or -not (Test-Path $sdkmanager)) {
        # cmdline-tools;<version> for Windows from Google's repository catalogue.
        $catalogue = Join-Path $work 'repository.xml'
        Download "$Repository/repository2-3.xml" $catalogue
        $xml = [xml](Get-Content -Raw $catalogue)
        $archives = $xml.SelectNodes("//*[local-name()='remotePackage'][@path='cmdline-tools;$CmdlineTools']/archives/archive") |
            Where-Object { $_.'host-os' -eq 'windows' }
        $chosen = @($archives | Where-Object { $_.'host-arch' -eq $arch }) + @($archives | Where-Object { -not $_.'host-arch' }) | Select-Object -First 1
        if (-not $chosen) { throw "cmdline-tools $CmdlineTools for Windows not found in $Repository/repository2-3.xml" }
        $file = $chosen.complete.url
        $zip = Join-Path $work 'cmdline-tools.zip'
        Download "$Repository/$file" $zip
        if ((Get-FileHash -Algorithm SHA1 $zip).Hash -ne $chosen.complete.checksum.'#text'.ToUpperInvariant()) { throw "cmdline-tools checksum mismatch ($file)" }
        Expand-Archive $zip (Join-Path $work 'cmdline-tools')
        if (Test-Path $toolsDir) { Remove-Item -Recurse -Force $toolsDir }
        New-Item -ItemType Directory -Force (Join-Path $Dir 'cmdline-tools') | Out-Null
        Move-Item (Join-Path $work 'cmdline-tools\cmdline-tools') $toolsDir
        Write-Host "Command-line tools $CmdlineTools installed: $toolsDir ($file)"
    } else {
        Write-Host "Already installed: cmdline-tools $CmdlineTools"
    }

    # --- Packages ------------------------------------------------------------------------------
    $imageAbi = if ($arch -eq 'aarch64') { 'arm64-v8a' } else { 'x86_64' }
    $image = "system-images;android-34;google_apis;$imageAbi"
    # A package counts as installed when its marker file exists; sdkmanager runs only for the others.
    $wanted = [ordered]@{
        'platform-tools' = 'platform-tools\adb.exe'
        "platforms;$Platform" = "platforms\$Platform\android.jar"
        "build-tools;$BuildTools" = "build-tools\$BuildTools\aapt2.exe"
        "ndk;$Ndk" = "ndk\$Ndk\source.properties"
    }
    if ($Emulator) {
        $wanted['emulator'] = 'emulator\emulator.exe'
        # The AVD's target (android-34): avdmanager does not list an AVD whose platform is missing.
        $wanted['platforms;android-34'] = 'platforms\android-34\android.jar'
        $wanted[$image] = "system-images\android-34\google_apis\$imageAbi\system.img"
    }
    $packages = @($wanted.Keys | Where-Object { $Force -or -not (Test-Path (Join-Path $Dir $wanted[$_])) })
    $wanted.Keys | Where-Object { $packages -notcontains $_ } | ForEach-Object { Write-Host "Already installed: $_" }
    $yes = ((1..100) | ForEach-Object { 'y' }) -join "`n"
    $ErrorActionPreference = 'Continue'   # sdkmanager prints warnings to stderr; results are checked below
    # Licenses: sdkmanager up to 22.0 needs them accepted first; 23.0+ says --licenses is no longer needed.
    if ($toolsMajor -lt 23) { $yes | & $sdkmanager "--sdk_root=$Dir" --licenses | Out-Null }
    # Network or DNS failures ("An error occurred while preparing SDK package ...: dl.google.com") leave
    # packages out: the missing ones are installed again, up to 3 attempts, 5 then 10 seconds apart.
    # BEAST_INSTALL_ANDROID_FAIL_ATTEMPTS=<n> (tests) sends the first n attempts through an unreachable proxy.
    $failAttempts = 0; [void][int]::TryParse("$env:BEAST_INSTALL_ANDROID_FAIL_ATTEMPTS", [ref]$failAttempts)
    for ($attempt = 1; $packages.Count -gt 0; $attempt++) {
        if ($attempt -eq 1) { Write-Host "Installing: $($packages -join ', ')" } else { Write-Host "Installing (attempt $attempt of 3): $($packages -join ', ')" }
        # A package file, not arguments: cmd.exe (sdkmanager.bat) splits "platforms;android-35" at ';'.
        $packageFile = Join-Path $work 'packages.txt'
        [IO.File]::WriteAllLines($packageFile, [string[]]$packages)
        $proxy = if ($attempt -le $failAttempts) { @('--proxy=http', '--proxy_host=127.0.0.1', '--proxy_port=9') } else { @() }
        $yes | & $sdkmanager "--sdk_root=$Dir" @proxy "--package_file=$packageFile" 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -notmatch '^\[=*' } | Write-Host
        $packages = @($packages | Where-Object { -not (Test-Path (Join-Path $Dir $wanted[$_])) })
        if ($packages.Count -eq 0) { break }
        if ($attempt -ge 3) {
            $ErrorActionPreference = 'Stop'
            throw "sdkmanager did not install $($packages -join ', ') after 3 attempts ($(Join-Path $Dir $wanted[$packages[0]]) is missing). The cause is most likely the network: check the connection to dl.google.com (DNS, proxy, firewall) and run the script again."
        }
        Write-Host "Not installed: $($packages -join ', ') (probably a network error reaching dl.google.com); retrying in $($attempt * 5) s..."
        Start-Sleep -Seconds ($attempt * 5)
    }
    $ErrorActionPreference = 'Stop'

    $ErrorActionPreference = 'Stop'

    # --- AVD -----------------------------------------------------------------------------------
    if ($Emulator) {
        $avdmanager = Join-Path $toolsDir 'bin\avdmanager.bat'
        $env:ANDROID_HOME = $Dir; $env:ANDROID_SDK_ROOT = $Dir
        # One AVD directory for avdmanager and the emulator (their defaults differ between versions).
        if (-not $env:ANDROID_AVD_HOME) { $env:ANDROID_AVD_HOME = Join-Path $env:USERPROFILE '.android\avd' }
        New-Item -ItemType Directory -Force $env:ANDROID_AVD_HOME | Out-Null
        $ErrorActionPreference = 'Continue'
        # The emulator's own list ('run --avd' starts the AVD through it; avdmanager's list is less reliable).
        $existing = @(& (Join-Path $Dir 'emulator\emulator.exe') -list-avds 2>$null)
        if ($Force -or $existing -notcontains $Avd) {
            # Quoted for cmd.exe (avdmanager.bat), which would split the package path at ';'.
            'no' | & $avdmanager create avd --force -n $Avd -k "`"$image`"" -d pixel_6 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "avdmanager could not create $Avd" }
            Write-Host "AVD created: $Avd ($image)"
        } else { Write-Host "Already created: AVD $Avd" }
        $ErrorActionPreference = 'Stop'
    }

    # --- Environment ---------------------------------------------------------------------------
    if (-not $NoProfile) {
        [Environment]::SetEnvironmentVariable('ANDROID_HOME', $Dir, 'User')
        [Environment]::SetEnvironmentVariable('JAVA_HOME', $jdk, 'User')
        if ($Emulator) { [Environment]::SetEnvironmentVariable('ANDROID_AVD_HOME', $env:ANDROID_AVD_HOME, 'User') }
        $tools = Join-Path $Dir 'platform-tools'
        $entries = @(([Environment]::GetEnvironmentVariable('Path', 'User')) -split ';' | Where-Object { $_ })
        if ($entries -notcontains $tools) { [Environment]::SetEnvironmentVariable('Path', (@($entries) + $tools) -join ';', 'User') }
        Write-Host 'Set ANDROID_HOME and JAVA_HOME and added platform-tools to the user PATH (open a new terminal).'
    }
    Write-Host ''
    Write-Host "Beast uses (defaults from ANDROID_HOME and JAVA_HOME$(if ($NoProfile) { '; set them or Config/BuildConfig.json' })):"
    Write-Host "  android.sdkRoot  = $Dir"
    Write-Host "  android.ndkRoot  = $(Join-Path $Dir 'ndk')   (newest; this script installed $Ndk)"
    Write-Host "  android.javaHome = $jdk"
    if ($Emulator) { Write-Host "  android.defaultAvd = $Avd" }
    if ($NoProfile) {
        $json = @{ sdkRoot = $Dir; ndkRoot = (Join-Path $Dir 'ndk'); javaHome = $jdk } | ConvertTo-Json -Compress
        Write-Host "`nConfig/BuildConfig.json:`n  `"android`": $json"
    }
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
exit 0
