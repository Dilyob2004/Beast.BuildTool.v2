using System.Diagnostics;

namespace Beast.BuildTool;

/// <summary>
/// Native macOS arm64/x86_64 with Apple clang (Xcode or the Command Line Tools), found through
/// xcrun. Programs and .dylib files carry rpaths @loader_path and @loader_path/../lib; dylibs are
/// named @rpath/lib&lt;Name&gt;.dylib, so dependencies beside them or in ../lib are found.
/// </summary>
public sealed class MacPlatform : UnixPlatform
{
    public override PlatformType Type => PlatformType.Mac;
    public override string SharedLibraryFileExtension => ".dylib";
    /// <summary>Apple silicon (arm64) uses "Mac", Intel (x86_64) "MacX64".</summary>
    public override string PlatformName => Architecture == PlatformArch.X64 ? "MacX64" : "Mac";
    public override string Compiler { get; }
    protected override string Archiver { get; }
    public override bool IsClang => true;
    /// <summary>Intel programs also run on Apple silicon through Rosetta 2.</summary>
    public override bool CanRunOnHost => HostPlatform.IsMacOS && (Architecture == HostPlatform.Architecture || Architecture == PlatformArch.X64);
    protected override string Label => "macOS " + ArchName;
    public string ArchName => Architecture == PlatformArch.X64 ? "x86_64" : "arm64";
    public string SdkRoot { get; }
    public string DeploymentTarget { get; }
    private readonly string _dsymutil;

    public MacPlatform(PlatformArch architecture = PlatformArch.ARM64) : base(architecture)
    {
        if (!HostPlatform.IsMacOS)
            throw new PlatformNotSupportedException("macOS targets require a Mac with Xcode or the Command Line Tools (xcode-select --install).");
        var settings = BuildConfigManager.Config.Mac;
        Compiler = Xcrun("--find", "clang++");
        Archiver = Xcrun("--find", "libtool");
        _dsymutil = Xcrun("--find", "dsymutil");
        SdkRoot = string.IsNullOrWhiteSpace(settings.SdkRoot) ? Xcrun("--show-sdk-path") : Environment.ExpandEnvironmentVariables(settings.SdkRoot);
        if (!Directory.Exists(SdkRoot)) throw new DirectoryNotFoundException($"macOS SDK was not found: {SdkRoot}");
        DeploymentTarget = string.IsNullOrWhiteSpace(settings.DeploymentTarget) ? "11.0" : settings.DeploymentTarget.Trim();
        Log.TraceVerbose($"macOS toolchain: {Compiler}, SDK {SdkRoot}, minimum macOS {DeploymentTarget}");
    }

    /// <summary>Runs xcrun for the macosx SDK and returns its single output line.</summary>
    private static string Xcrun(params string[] arguments)
    {
        var info = new ProcessStartInfo("xcrun") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("--sdk");
        info.ArgumentList.Add("macosx");
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("xcrun could not be started.");
            string output = process.StandardOutput.ReadToEnd().Trim();
            string error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0 || output.Length == 0)
                throw new InvalidOperationException($"xcrun {string.Join(' ', arguments)} failed: {error}. Install Xcode or the Command Line Tools (xcode-select --install).");
            return output.Split('\n')[0].Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new FileNotFoundException("xcrun was not found. Install Xcode or the Command Line Tools (xcode-select --install).");
        }
    }

    protected override IEnumerable<string> PlatformDefinitions => ["PLATFORM_MAC", "PLATFORM_APPLE"];

    protected override IEnumerable<string> TargetArguments =>
        ["-arch", ArchName, "-isysroot", "\"" + SdkRoot + "\"", "-mmacosx-version-min=" + DeploymentTarget];

    protected override string SharedLibraryFlag => "-dynamiclib";

    protected override IEnumerable<string> PlatformLinkArguments(LinkEnvironment environment, string output)
    {
        if (environment.StaticRuntime)
            throw new InvalidOperationException("StaticRuntime is not available on macOS: libc++ is part of the operating system.");
        var args = new List<string> { "-Wl,-dead_strip", "-Wl,-rpath,@loader_path", "-Wl,-rpath,@loader_path/../lib" };
        // A bundle's program (X.app/Contents/MacOS/X) also finds dylibs in Contents/Frameworks, beside
        // the bundle (Binaries/...) and in an install prefix (bin/X.app -> lib/).
        if (IsBundleExecutable(output))
            args.AddRange(["-Wl,-rpath,@executable_path/../Frameworks", "-Wl,-rpath,@loader_path/../../..", "-Wl,-rpath,@loader_path/../../../../lib"]);
        if (environment.Output == LinkerOutput.SharedLibrary)
            args.Add("-install_name @rpath/" + Path.GetFileName(output));
        return args;
    }

    public override void LinkFiles(CommandExecutor executor, LinkEnvironment environment, string outputFilePath)
    {
        base.LinkFiles(executor, environment, outputFilePath);
        // Optimized configurations get a .dSYM bundle beside the product (like Xcode's "DWARF with
        // dSYM"); Debug builds are debugged from the object files referenced by the debug map.
        if (!environment.Optimization) return;
        string output = environment.Output == LinkerOutput.SharedLibrary && !outputFilePath.EndsWith(".dylib", StringComparison.Ordinal)
            ? outputFilePath + ".dylib" : outputFilePath;
        string dsym = DsymPath(output);
        string dwarf = Path.Combine(dsym, "Contents", "Resources", "DWARF", Path.GetFileName(output));
        executor.AddCommand(new CommandContext(_dsymutil, $"\"{output}\" -o \"{dsym}\"", CommandType.Link, [dwarf], [output], false,
            $"Writing {Path.GetFileName(dsym)}...", recreateOutputs: true));
    }

    public static string DsymPath(string product) => (IsBundleExecutable(product) ? BundleOf(product) : product) + ".dSYM";

    // ------------------------------------------------------------------ application bundles

    /// <summary>Application targets with ConsoleApplication = false are built as X.app bundles.</summary>
    public static string BundlePath(string binaries, string target) => Path.Combine(binaries, target + ".app");
    public static string BundleExecutable(string binaries, string target) => Path.Combine(BundlePath(binaries, target), "Contents", "MacOS", target);
    public static bool IsBundleExecutable(string path) => path.Replace('\\', '/').Contains(".app/Contents/MacOS/", StringComparison.Ordinal);
    private static string BundleOf(string executable) => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!)!;

    /// <summary>The bundle's program when the target was built as a bundle, otherwise the plain executable.</summary>
    public override string ExecutablePath(string binaries, string target) =>
        File.Exists(BundleExecutable(binaries, target)) ? BundleExecutable(binaries, target) : base.ExecutablePath(binaries, target);

    /// <summary>Writes Contents/Info.plist (only when it changes) and creates Contents/Resources.</summary>
    public void WriteBundleInfo(string binaries, string target, string version)
    {
        string contents = Path.Combine(BundlePath(binaries, target), "Contents");
        Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
        Directory.CreateDirectory(Path.Combine(contents, "Resources"));
        string identifier = string.IsNullOrWhiteSpace(BuildConfigManager.Config.Mac.BundleIdentifier)
            ? "com.beast." + new string(target.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is '-' or '.').ToArray())
            : BuildConfigManager.Config.Mac.BundleIdentifier;
        string shortVersion = version.Length > 0 ? version : "1.0";
        static string Escape(string value) => System.Security.SecurityElement.Escape(value);
        ModuleCompilation.WriteIfChanged(Path.Combine(contents, "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleDevelopmentRegion</key><string>en</string>
              <key>CFBundleExecutable</key><string>{Escape(target)}</string>
              <key>CFBundleIdentifier</key><string>{Escape(identifier)}</string>
              <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
              <key>CFBundleName</key><string>{Escape(target)}</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleShortVersionString</key><string>{Escape(shortVersion)}</string>
              <key>CFBundleVersion</key><string>{Escape(shortVersion)}</string>
              <key>LSMinimumSystemVersion</key><string>{Escape(DeploymentTarget)}</string>
              <key>NSHighResolutionCapable</key><true/>
              <key>NSPrincipalClass</key><string>NSApplication</string>
            </dict>
            </plist>

            """);
    }

    /// <summary>
    /// Signs an installed bundle or dylib (mac.signingIdentity, ad-hoc "-" by default). Builds are not
    /// re-signed: the linker's ad-hoc signature is enough to run them on this Mac.
    /// </summary>
    public static async Task SignAsync(string bundle)
    {
        string identity = string.IsNullOrWhiteSpace(BuildConfigManager.Config.Mac.SigningIdentity) ? "-" : BuildConfigManager.Config.Mac.SigningIdentity;
        var info = new ProcessStartInfo("codesign") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        // The hardened runtime (needed for notarization) only with a real identity: with an ad-hoc
        // signature its library validation would reject the project's own ad-hoc signed dylibs.
        var arguments = identity == "-" ? new[] { "--force", "--sign", "-", bundle } : new[] { "--force", "--sign", identity, "--options", "runtime", "--timestamp", bundle };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("codesign could not be started.");
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"codesign failed for {bundle}: {error}");
        Log.TraceInfo($"Signed {Path.GetFileName(bundle)} ({(identity == "-" ? "ad-hoc" : identity)}).");
    }

    public override void ArchiveFiles(CommandExecutor executor, List<string> objects, string outputPath)
    {
        // Apple libtool reads long object lists from -filelist (it has no @response files).
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string list = CommandUtilities.WriteResponseFile(outputPath, string.Join("\n", objects));
        executor.AddCommand(new CommandContext(Archiver, $"-static -no_warning_for_no_symbols -o \"{outputPath}\" -filelist \"{list}\"",
            CommandType.Link, [outputPath], objects.Append(list).ToList(), false, $"Archiving {Path.GetFileName(outputPath)}...", recreateOutputs: true));
    }

    /// <summary>Frameworks of the SDK: ModuleRules.Frameworks.Add("Cocoa") links -framework Cocoa.</summary>
    public override void SetupLinkEnvironment(LinkEnvironment environment) { }
}
