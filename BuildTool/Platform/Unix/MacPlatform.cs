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
    private readonly bool _appleClang;

    /// <summary>Apple clang does not implement C++20 named modules (the keywords are not recognized).</summary>
    public override string? ModuleInterfaceExtension => _appleClang ? null : ".pcm";
    public override string ModulesUnsupportedReason =>
        "Apple clang does not support C++20 named modules; install LLVM (brew install llvm) and set \"mac\": { \"cxx\": \"/opt/homebrew/opt/llvm/bin/clang++\" }";

    internal static bool IsAppleClang(string compiler)
    {
        try
        {
            var info = new ProcessStartInfo(compiler, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(info)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output.Contains("Apple clang", StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    public MacPlatform(PlatformArch architecture = PlatformArch.ARM64) : base(architecture)
    {
        if (!HostPlatform.IsMacOS)
            throw new PlatformNotSupportedException("macOS targets require a Mac with Xcode or the Command Line Tools (xcode-select --install).");
        var settings = BuildConfigManager.Config.Mac;
        // mac.cxx: another Clang (e.g. Homebrew LLVM, which supports C++20 modules) with the Xcode SDK.
        Compiler = string.IsNullOrWhiteSpace(settings.Cxx) ? Xcrun("--find", "clang++")
            : UnixPlatform.FindOnPath(Environment.ExpandEnvironmentVariables(settings.Cxx)) ?? throw new FileNotFoundException("mac.cxx was not found.", settings.Cxx);
        _appleClang = IsAppleClang(Compiler);
        Archiver = Xcrun("--find", "libtool");
        _dsymutil = Xcrun("--find", "dsymutil");
        SdkRoot = string.IsNullOrWhiteSpace(settings.SdkRoot) ? Xcrun("--show-sdk-path") : Environment.ExpandEnvironmentVariables(settings.SdkRoot);
        if (!Directory.Exists(SdkRoot)) throw new DirectoryNotFoundException($"macOS SDK was not found: {SdkRoot}");
        DeploymentTarget = string.IsNullOrWhiteSpace(settings.DeploymentTarget) ? "11.0" : settings.DeploymentTarget.Trim();
        Log.TraceVerbose($"macOS toolchain: {Compiler}, SDK {SdkRoot}, minimum macOS {DeploymentTarget}");
    }

    private static string Xcrun(params string[] arguments) => XcrunSdk("macosx", arguments);

    /// <summary>Runs xcrun for an SDK (macosx, iphoneos, iphonesimulator) and returns its single output line.</summary>
    internal static string XcrunSdk(string sdk, params string[] arguments)
    {
        var info = new ProcessStartInfo("xcrun") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("--sdk");
        info.ArgumentList.Add(sdk);
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
            $"Writing {Path.GetFileName(dsym)}...", recreateOutputs: true, statePath: dsym + ".state.json"));
    }

    /// <summary>
    /// Build state stays outside bundles: a file in X.app/Contents/MacOS would be an unsigned
    /// subcomponent and make codesign fail.
    /// </summary>
    protected override string? LinkStatePath(string output) => IsBundleExecutable(output) ? BundleOf(output) + ".state.json" : null;

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

    public override void ArchiveFiles(CommandExecutor executor, List<string> objects, string outputPath) => AddLibtoolArchive(executor, Archiver, objects, outputPath);

    /// <summary>Apple libtool reads long object lists from -filelist (it has no @response files).</summary>
    internal static void AddLibtoolArchive(CommandExecutor executor, string libtool, List<string> objects, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string list = CommandUtilities.WriteResponseFile(outputPath, string.Join("\n", objects));
        executor.AddCommand(new CommandContext(libtool, $"-static -no_warning_for_no_symbols -o \"{outputPath}\" -filelist \"{list}\"",
            CommandType.Link, [outputPath], objects.Append(list).ToList(), false, $"Archiving {Path.GetFileName(outputPath)}...", recreateOutputs: true));
    }

    // ------------------------------------------------------------------ universal binaries

    /// <summary>--arch universal: an arm64 and an x86_64 build of the same targets.</summary>
    public static bool IsUniversal(IReadOnlyList<Platform> platforms) =>
        platforms.Count == 2 && platforms.All(p => p is MacPlatform) && platforms.Select(p => p.Architecture).Distinct().Count() == 2;

    public const string UniversalName = "MacUniversal";

    /// <summary>Binaries/Mac/... -> Binaries/MacUniversal/... (other paths unchanged).</summary>
    public static string ToUniversal(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        string marker = separator + "Binaries" + separator + "Mac" + separator;
        int index = path.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? path : path[..index] + separator + "Binaries" + separator + UniversalName + separator + path[(index + marker.Length)..];
    }

    /// <summary>
    /// Merges Binaries/Mac/&lt;config&gt; (arm64) and Binaries/MacX64/&lt;config&gt; into
    /// Binaries/MacUniversal/&lt;config&gt;: Mach-O files present in both (programs, dylibs, archives,
    /// bundle programs, dSYM DWARF) are combined with lipo; other files are copied from the arm64 tree.
    /// Only files newer than the merged copy are processed.
    /// </summary>
    public static async Task MergeUniversalAsync(string project, TargetConfiguration configuration)
    {
        string arm = Path.Combine(project, "Binaries", "Mac", configuration.ToString());
        string intel = Path.Combine(project, "Binaries", "MacX64", configuration.ToString());
        string universal = Path.Combine(project, "Binaries", UniversalName, configuration.ToString());
        string lipo = XcrunSdk("macosx", "--find", "lipo");
        int merged = 0;
        foreach (string file in Directory.EnumerateFiles(arm, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".state.json", StringComparison.Ordinal)) continue;
            string relative = Path.GetRelativePath(arm, file);
            string destination = Path.Combine(universal, relative);
            string counterpart = Path.Combine(intel, relative);
            bool macho = File.Exists(counterpart) && IsMachO(file) && IsMachO(counterpart);
            DateTime newest = macho ? new[] { File.GetLastWriteTimeUtc(file), File.GetLastWriteTimeUtc(counterpart) }.Max() : File.GetLastWriteTimeUtc(file);
            if (File.Exists(destination) && File.GetLastWriteTimeUtc(destination) >= newest) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (macho)
            {
                var info = new ProcessStartInfo(lipo) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (string argument in new[] { "-create", file, counterpart, "-output", destination }) info.ArgumentList.Add(argument);
                using var process = Process.Start(info)!;
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new InvalidOperationException($"lipo failed for {relative}: {error}");
                ++merged;
            }
            else File.Copy(file, destination, true);
        }
        if (merged > 0) Log.TraceInfo($"Universal binaries: {merged} merged into {universal}");
    }

    /// <summary>Thin or fat Mach-O file (programs, dylibs, objects) or a static archive.</summary>
    private static bool IsMachO(string path)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < 8) return false;
        uint magic = BitConverter.ToUInt32(header[..4]);
        return magic is 0xfeedfacf or 0xcffaedfe or 0xfeedface or 0xcefaedfe or 0xcafebabe or 0xbebafeca
            || System.Text.Encoding.ASCII.GetString(header) == "!<arch>\n";
    }

    // ------------------------------------------------------------------ disk images

    /// <summary>
    /// package --dmg: a compressed disk image of the installed files. Applications are placed at the top
    /// with an Applications link, and the dylibs from lib/ are embedded in their Contents/Frameworks
    /// (programs find them through @executable_path/../Frameworks) before the bundle is signed again.
    /// With --notarize the image is signed, submitted with notarytool (mac.notaryProfile) and stapled.
    /// </summary>
    public static async Task<string> CreateDiskImageAsync(string staging, string output, string volume, bool notarize)
    {
        var settings = BuildConfigManager.Config.Mac;
        if (notarize && (string.IsNullOrWhiteSpace(settings.SigningIdentity) || string.IsNullOrWhiteSpace(settings.NotaryProfile)))
            throw new InvalidOperationException("--notarize needs mac.signingIdentity (\"Developer ID Application: ...\") and mac.notaryProfile " +
                "(a keychain profile from 'xcrun notarytool store-credentials').");
        string root = staging + ".dmg-root";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string bin = Path.Combine(staging, "bin");
        var apps = Directory.Exists(bin) ? Directory.GetDirectories(bin, "*.app") : [];
        string lib = Path.Combine(staging, "lib");
        foreach (string app in apps)
        {
            string target = Path.Combine(root, Path.GetFileName(app));
            await RunToolAsync("/bin/cp", ["-R", app, target]);
            if (Directory.Exists(lib))
                foreach (string dylib in Directory.GetFiles(lib, "*.dylib"))
                {
                    string frameworks = Path.Combine(target, "Contents", "Frameworks");
                    Directory.CreateDirectory(frameworks);
                    File.Copy(dylib, Path.Combine(frameworks, Path.GetFileName(dylib)), true);
                    if (!string.IsNullOrWhiteSpace(settings.SigningIdentity)) await SignAsync(Path.Combine(frameworks, Path.GetFileName(dylib)));
                }
            await SignAsync(target);
        }
        if (apps.Length > 0) File.CreateSymbolicLink(Path.Combine(root, "Applications"), "/Applications");
        // Everything else (command line programs, libraries, headers) keeps the install layout.
        foreach (string entry in Directory.EnumerateFileSystemEntries(staging))
        {
            string name = Path.GetFileName(entry);
            if (apps.Length > 0 && name == "bin" && Directory.GetFileSystemEntries(bin).All(e => e.EndsWith(".app", StringComparison.Ordinal))) continue;
            await RunToolAsync("/bin/cp", ["-R", entry, Path.Combine(root, name)]);
        }
        if (apps.Length > 0 && Directory.Exists(Path.Combine(root, "bin")))
            foreach (string app in Directory.GetDirectories(Path.Combine(root, "bin"), "*.app")) Directory.Delete(app, true);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (File.Exists(output)) File.Delete(output);
        await RunToolAsync("/usr/bin/hdiutil", ["create", "-volname", volume, "-srcfolder", root, "-ov", "-format", "UDZO", output]);
        Directory.Delete(root, true);
        if (!string.IsNullOrWhiteSpace(settings.SigningIdentity))
            await RunToolAsync("/usr/bin/codesign", ["--force", "--sign", settings.SigningIdentity, "--timestamp", output]);
        if (notarize)
        {
            Log.TraceInfo("Submitting the disk image for notarization (this can take several minutes)...");
            await RunToolAsync("/usr/bin/xcrun", ["notarytool", "submit", output, "--keychain-profile", settings.NotaryProfile, "--wait"],
                failure: output => output.Contains("status: Accepted", StringComparison.Ordinal) ? null : "notarization was not accepted");
            await RunToolAsync("/usr/bin/xcrun", ["stapler", "staple", output]);
            Log.TraceInfo("Notarized and stapled.");
        }
        Log.TraceInfo($"Disk image: {output}");
        return output;
    }

    private static async Task RunToolAsync(string program, IEnumerable<string> arguments, Func<string, string?>? failure = null)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{program} could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = (await stdout) + (await stderr);
        string? problem = process.ExitCode != 0 ? $"exit code {process.ExitCode}" : failure?.Invoke(output);
        if (problem != null) throw new InvalidOperationException($"{Path.GetFileName(program)} {string.Join(' ', arguments)}: {problem}{Environment.NewLine}{output}");
    }

    /// <summary>Frameworks of the SDK: ModuleRules.Frameworks.Add("Cocoa") links -framework Cocoa.</summary>
    public override void SetupLinkEnvironment(LinkEnvironment environment) { }
}
