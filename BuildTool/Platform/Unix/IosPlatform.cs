using System.Diagnostics;
using System.Text.Json;

namespace Beast.BuildTool;

/// <summary>
/// iOS devices (arm64, SDK iphoneos) and the iOS Simulator (arm64 or x86_64, SDK iphonesimulator)
/// with Apple clang through xcrun. Applications are .app bundles (flat iOS layout: X.app/X); the
/// .dylib files of shared dependency targets are embedded in X.app/Frameworks. Test targets are plain
/// executables run in the simulator with simctl spawn.
/// </summary>
public sealed class IosPlatform : UnixPlatform
{
    public override PlatformType Type => PlatformType.IOS;
    public override string SharedLibraryFileExtension => ".dylib";
    public bool Simulator { get; }
    /// <summary>"IOS" (device), "IOSSimulator" (arm64 simulator), "IOSSimulatorX64".</summary>
    public override string PlatformName => !Simulator ? "IOS" : Architecture == PlatformArch.X64 ? "IOSSimulatorX64" : "IOSSimulator";
    public override string Compiler { get; }
    protected override string Archiver { get; }
    public override bool IsClang => true;
    /// <summary>Simulator executables run on this Mac (through simctl); device builds do not.</summary>
    public override bool CanRunOnHost => false;
    protected override string Label => Simulator ? "iOS Simulator " + ArchName : "iOS";
    public string ArchName => Architecture == PlatformArch.X64 ? "x86_64" : "arm64";
    public string Sdk => Simulator ? "iphonesimulator" : "iphoneos";
    public string SdkRoot { get; }
    public string DeploymentTarget { get; }
    public override string? ModuleInterfaceExtension => _appleClang ? null : ".pcm";
    public override string ModulesUnsupportedReason =>
        "Apple clang does not support C++20 named modules; set \"ios\": { \"cxx\": \"<LLVM clang++>\" } to use another Clang";
    private readonly bool _appleClang;
    private readonly string _dsymutil;

    public IosPlatform(bool simulator, PlatformArch architecture = PlatformArch.ARM64) : base(architecture)
    {
        if (!HostPlatform.IsMacOS)
            throw new PlatformNotSupportedException("iOS targets require a Mac with Xcode (the Command Line Tools have no iOS SDK).");
        if (!simulator && architecture != PlatformArch.ARM64)
            throw new ArgumentException("iOS devices are arm64. Use --platform IOSSimulator --arch x64 for an Intel simulator.");
        Simulator = simulator;
        var settings = BuildConfigManager.Config.Ios;
        Compiler = string.IsNullOrWhiteSpace(settings.Cxx) ? MacPlatform.XcrunSdk(Sdk, "--find", "clang++")
            : FindOnPath(Environment.ExpandEnvironmentVariables(settings.Cxx)) ?? throw new FileNotFoundException("ios.cxx was not found.", settings.Cxx);
        _appleClang = MacPlatform.IsAppleClang(Compiler);
        Archiver = MacPlatform.XcrunSdk(Sdk, "--find", "libtool");
        _dsymutil = MacPlatform.XcrunSdk(Sdk, "--find", "dsymutil");
        SdkRoot = MacPlatform.XcrunSdk(Sdk, "--show-sdk-path");
        DeploymentTarget = string.IsNullOrWhiteSpace(settings.DeploymentTarget) ? "15.0" : settings.DeploymentTarget.Trim();
        Log.TraceVerbose($"iOS toolchain: {Compiler}, SDK {SdkRoot}, minimum iOS {DeploymentTarget}");
    }

    public string Triple => $"{ArchName}-apple-ios{DeploymentTarget}" + (Simulator ? "-simulator" : "");

    protected override IEnumerable<string> PlatformDefinitions => ["PLATFORM_IOS", "PLATFORM_APPLE"];
    protected override IEnumerable<string> TargetArguments => ["--target=" + Triple, "-isysroot", "\"" + SdkRoot + "\""];
    protected override string SharedLibraryFlag => "-dynamiclib";

    protected override IEnumerable<string> PlatformLinkArguments(LinkEnvironment environment, string output)
    {
        if (environment.StaticRuntime)
            throw new InvalidOperationException("StaticRuntime is not available on iOS: libc++ is part of the operating system.");
        // Programs: dylibs in X.app/Frameworks; test executables: beside them. Dylibs: @rpath names.
        var args = new List<string> { "-Wl,-dead_strip", "-Wl,-rpath,@executable_path/Frameworks", "-Wl,-rpath,@loader_path" };
        if (environment.Output == LinkerOutput.SharedLibrary) args.Add("-install_name @rpath/" + Path.GetFileName(output));
        return args;
    }

    public override void ArchiveFiles(CommandExecutor executor, List<string> objects, string outputPath) =>
        MacPlatform.AddLibtoolArchive(executor, Archiver, objects, outputPath);

    // ------------------------------------------------------------------ bundles

    public static string BundlePath(string binaries, string target) => Path.Combine(binaries, target + ".app");
    public static string BundleExecutable(string binaries, string target) => Path.Combine(BundlePath(binaries, target), target);

    public override string ExecutablePath(string binaries, string target) =>
        File.Exists(BundleExecutable(binaries, target)) ? BundleExecutable(binaries, target) : base.ExecutablePath(binaries, target);

    public static string BundleIdentifier(string target)
    {
        string configured = BuildConfigManager.Config.Ios.BundleIdentifier;
        return !string.IsNullOrWhiteSpace(configured) ? configured
            : "com.beast." + new string(target.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is '-' or '.').ToArray());
    }

    /// <summary>
    /// Plans X.app: Info.plist, then one action that copies the linked program and the embedded
    /// dylibs into the bundle and signs it (simulator: ad hoc; device: ios.signingIdentity with the
    /// provisioning profile's entitlements). Signing a copy keeps the link output unchanged, so
    /// builds stay incremental.
    /// </summary>
    public void AddBundle(CommandExecutor executor, string binaries, string buildPath, string target, string version, string program, IReadOnlyList<string> frameworks)
    {
        string bundle = BundlePath(binaries, target);
        Directory.CreateDirectory(bundle);
        string plist = Path.Combine(bundle, "Info.plist");
        string shortVersion = version.Length > 0 ? version : "1.0";
        static string Escape(string value) => System.Security.SecurityElement.Escape(value);
        ModuleCompilation.WriteIfChanged(plist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleDevelopmentRegion</key><string>en</string>
              <key>CFBundleExecutable</key><string>{Escape(target)}</string>
              <key>CFBundleIdentifier</key><string>{Escape(BundleIdentifier(target))}</string>
              <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
              <key>CFBundleName</key><string>{Escape(target)}</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleShortVersionString</key><string>{Escape(shortVersion)}</string>
              <key>CFBundleVersion</key><string>{Escape(shortVersion)}</string>
              <key>CFBundleSupportedPlatforms</key><array><string>{(Simulator ? "iPhoneSimulator" : "iPhoneOS")}</string></array>
              <key>DTPlatformName</key><string>{Sdk}</string>
              <key>MinimumOSVersion</key><string>{Escape(DeploymentTarget)}</string>
              <key>LSRequiresIPhoneOS</key><true/>
              <key>UIDeviceFamily</key><array><integer>1</integer><integer>2</integer></array>
              <key>UIRequiredDeviceCapabilities</key><array><string>arm64</string></array>
              <key>UILaunchScreen</key><dict/>
              <key>UISupportedInterfaceOrientations</key><array><string>UIInterfaceOrientationPortrait</string><string>UIInterfaceOrientationLandscapeLeft</string><string>UIInterfaceOrientationLandscapeRight</string></array>
            </dict>
            </plist>

            """);
        var settings = BuildConfigManager.Config.Ios;
        string? profile = Simulator || string.IsNullOrWhiteSpace(settings.ProvisioningProfile) ? null : Environment.ExpandEnvironmentVariables(settings.ProvisioningProfile);
        if (!Simulator && (profile == null || string.IsNullOrWhiteSpace(settings.SigningIdentity)))
            throw new InvalidOperationException("iOS device builds must be signed: set ios.signingIdentity (\"Apple Development: ...\") and ios.provisioningProfile " +
                "(a .mobileprovision for this bundle identifier). The simulator needs neither: --platform IOSSimulator.");
        if (profile != null && !File.Exists(profile)) throw new FileNotFoundException("ios.provisioningProfile was not found.", profile);
        string signature = Path.Combine(bundle, "_CodeSignature", "CodeResources");
        var inputs = new List<string> { program, plist, typeof(IosPlatform).Assembly.Location };
        inputs.AddRange(frameworks);
        if (profile != null) inputs.Add(profile);
        string identity = Simulator ? "-" : settings.SigningIdentity;
        executor.AddCommand(new CommandContext("/usr/bin/codesign", $"bundle-v1 {identity}", CommandType.Copy, [signature], inputs, true,
            $"Signing {target}.app ({(Simulator ? "ad-hoc" : identity)})...", allowArtifactCache: false,
            statePath: bundle + ".state.json",
            execute: async token =>
            {
                File.Copy(program, BundleExecutable(binaries, target), true);
                string frameworksDirectory = Path.Combine(bundle, "Frameworks");
                if (Directory.Exists(frameworksDirectory)) Directory.Delete(frameworksDirectory, true);
                foreach (string framework in frameworks)
                {
                    Directory.CreateDirectory(frameworksDirectory);
                    string embedded = Path.Combine(frameworksDirectory, Path.GetFileName(framework));
                    File.Copy(framework, embedded, true);
                    await CodesignAsync(embedded, identity, null, token);
                }
                string? entitlements = null;
                if (profile != null)
                {
                    File.Copy(profile, Path.Combine(bundle, "embedded.mobileprovision"), true);
                    entitlements = Path.Combine(buildPath, target + ".entitlements");
                    await File.WriteAllTextAsync(entitlements, await EntitlementsAsync(profile, token), token);
                }
                await CodesignAsync(bundle, identity, entitlements, token);
            }));
    }

    private static async Task CodesignAsync(string path, string identity, string? entitlements, CancellationToken token)
    {
        var arguments = new List<string> { "--force", "--sign", identity, "--timestamp=none" };
        if (entitlements != null) arguments.AddRange(["--entitlements", entitlements]);
        arguments.Add(path);
        var (exit, output) = await RunAsync("codesign", arguments, token);
        if (exit != 0) throw new InvalidOperationException($"codesign failed for {path}: {output}");
    }

    /// <summary>The Entitlements dictionary of a provisioning profile (security cms -D + PlistBuddy).</summary>
    private static async Task<string> EntitlementsAsync(string profile, CancellationToken token)
    {
        var (exit, plist) = await RunAsync("security", ["cms", "-D", "-i", profile], token);
        if (exit != 0) throw new InvalidOperationException($"Cannot read provisioning profile {profile}: {plist}");
        string decoded = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(decoded, plist, token);
            var (code, entitlements) = await RunAsync("/usr/libexec/PlistBuddy", ["-x", "-c", "Print :Entitlements", decoded], token);
            if (code != 0) throw new InvalidOperationException($"The provisioning profile has no Entitlements: {entitlements}");
            return entitlements;
        }
        finally { File.Delete(decoded); }
    }

    internal static async Task<(int Exit, string Output)> RunAsync(string program, IEnumerable<string> arguments, CancellationToken token = default)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{program} could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, (await stdout) + (await stderr));
    }

    public override void LinkFiles(CommandExecutor executor, LinkEnvironment environment, string outputFilePath)
    {
        base.LinkFiles(executor, environment, outputFilePath);
        if (!environment.Optimization) return;
        string output = environment.Output == LinkerOutput.SharedLibrary && !outputFilePath.EndsWith(".dylib", StringComparison.Ordinal)
            ? outputFilePath + ".dylib" : outputFilePath;
        string dsym = output + ".dSYM";
        executor.AddCommand(new CommandContext(_dsymutil, $"\"{output}\" -o \"{dsym}\"", CommandType.Link,
            [Path.Combine(dsym, "Contents", "Resources", "DWARF", Path.GetFileName(output))], [output], false,
            $"Writing {Path.GetFileName(dsym)}...", recreateOutputs: true, statePath: dsym + ".state.json"));
    }

    public override void SetupLinkEnvironment(LinkEnvironment environment) { }
}

/// <summary>Runs iOS programs: the simulator through simctl, devices through devicectl (Xcode 15+).</summary>
public static class IosRunner
{
    /// <summary>The simulator to use: ios.simulator (name or UDID), else a booted one, else the first available iPhone.</summary>
    public static async Task<string> SimulatorAsync()
    {
        var (exit, json) = await IosPlatform.RunAsync("xcrun", ["simctl", "list", "devices", "available", "--json"]);
        if (exit != 0) throw new InvalidOperationException("xcrun simctl failed: " + json);
        using var document = JsonDocument.Parse(json);
        var devices = document.RootElement.GetProperty("devices").EnumerateObject()
            .Where(runtime => runtime.Name.Contains("iOS", StringComparison.Ordinal))
            .OrderByDescending(runtime => runtime.Name, StringComparer.Ordinal)
            .SelectMany(runtime => runtime.Value.EnumerateArray())
            .Select(d => (Udid: d.GetProperty("udid").GetString()!, Name: d.GetProperty("name").GetString()!, State: d.GetProperty("state").GetString()!)).ToList();
        string wanted = BuildConfigManager.Config.Ios.Simulator;
        var device = wanted.Length > 0
            ? devices.FirstOrDefault(d => d.Udid == wanted || d.Name == wanted)
            : devices.FirstOrDefault(d => d.State == "Booted") is { Udid: not null } booted ? booted : devices.FirstOrDefault(d => d.Name.StartsWith("iPhone", StringComparison.Ordinal));
        if (device.Udid == null)
            throw new InvalidOperationException(wanted.Length > 0 ? $"iOS simulator '{wanted}' was not found (xcrun simctl list devices)." : "No iOS simulator is installed. Add one in Xcode > Settings > Platforms.");
        if (device.State != "Booted")
        {
            Log.TraceInfo($"Booting iOS simulator {device.Name}...");
            var (code, output) = await IosPlatform.RunAsync("xcrun", ["simctl", "bootstatus", device.Udid, "-b"]);
            if (code != 0) throw new InvalidOperationException($"Simulator {device.Name} did not boot: {output}");
        }
        return device.Udid;
    }

    /// <summary>Installs and launches an app in the simulator; its console output is shown until it exits.</summary>
    public static async Task<int> RunSimulatorAppAsync(string bundle, string identifier)
    {
        string udid = await SimulatorAsync();
        var (exit, output) = await IosPlatform.RunAsync("xcrun", ["simctl", "install", udid, bundle]);
        if (exit != 0) throw new InvalidOperationException($"simctl install failed: {output}");
        return await Installer.RunLocalAsync("/usr/bin/xcrun", Path.GetDirectoryName(bundle)!, ["simctl", "launch", "--console-pty", "--terminate-running-process", udid, identifier]);
    }

    /// <summary>Runs a test executable inside the simulator (simctl spawn); returns its exit code.</summary>
    public static async Task<int> SpawnAsync(string executable, IEnumerable<string> arguments, int timeoutSeconds)
    {
        string udid = await SimulatorAsync();
        return await Installer.RunLocalAsync("/usr/bin/xcrun", Path.GetDirectoryName(executable)!, new[] { "simctl", "spawn", udid, executable }.Concat(arguments), timeoutSeconds);
    }

    /// <summary>Installs and launches a signed app on a connected device (xcrun devicectl, Xcode 15+).</summary>
    public static async Task<int> RunDeviceAppAsync(string bundle, string identifier, string? device)
    {
        string target = device ?? BuildConfigManager.Config.Ios.Device;
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("Pass --device <name or identifier> (xcrun devicectl list devices) or set ios.device.");
        var (exit, output) = await IosPlatform.RunAsync("xcrun", ["devicectl", "device", "install", "app", "--device", target, bundle]);
        if (exit != 0) throw new InvalidOperationException($"devicectl install failed: {output}");
        return await Installer.RunLocalAsync("/usr/bin/xcrun", Path.GetDirectoryName(bundle)!, ["devicectl", "device", "process", "launch", "--console", "--device", target, identifier]);
    }
}
