namespace Beast.BuildTool;

/// <summary>
/// Native Linux x86_64/arm64 with Clang (default when installed) or GCC. Executables and shared
/// libraries carry RUNPATH $ORIGIN:$ORIGIN/../lib, so dependency .so files beside them (Binaries)
/// or in ../lib (an install prefix) are found without LD_LIBRARY_PATH.
/// </summary>
public sealed class LinuxPlatform : UnixPlatform
{
    public override PlatformType Type => PlatformType.Linux;
    public override string SharedLibraryFileExtension => ".so";
    /// <summary>x86_64 uses "Linux", arm64 "LinuxArm64" (like Windows/WindowsArm64).</summary>
    public override string PlatformName => Architecture == PlatformArch.ARM64 ? "LinuxArm64" : "Linux";
    public override string Compiler { get; }
    protected override string Archiver { get; }
    public override bool IsClang { get; }
    public string Triple => (Architecture == PlatformArch.ARM64 ? "aarch64" : "x86_64") + "-linux-gnu";
    public override bool CanRunOnHost => HostPlatform.IsLinux && Architecture == HostPlatform.Architecture;
    protected override string Label => Architecture == HostPlatform.Architecture ? "Linux" : "Linux " + (Architecture == PlatformArch.ARM64 ? "arm64" : "x86_64");
    private readonly string _sysroot;
    private readonly bool _cross;
    private readonly string _linker;

    public LinuxPlatform(PlatformArch architecture = PlatformArch.X64) : base(architecture)
    {
        var settings = BuildConfigManager.Config.Linux;
        _sysroot = Environment.ExpandEnvironmentVariables(settings.Sysroot);
        _cross = architecture != HostPlatform.Architecture || !HostPlatform.IsLinux;
        if (_cross && !HostPlatform.IsLinux && string.IsNullOrWhiteSpace(_sysroot))
            throw new PlatformNotSupportedException("Linux targets on a non-Linux host need linux.sysroot (a Linux root file system) and Clang.");
        string compiler = settings.Compiler.Trim().ToLowerInvariant();
        if (compiler is not ("" or "clang" or "gcc"))
            throw new InvalidOperationException($"linux.compiler must be \"clang\", \"gcc\" or empty, not \"{settings.Compiler}\".");
        string? cxx = null;
        if (!string.IsNullOrWhiteSpace(settings.Cxx))
        {
            cxx = UnixPlatform.FindOnPath(Environment.ExpandEnvironmentVariables(settings.Cxx))
                ?? throw new FileNotFoundException("linux.cxx was not found.", settings.Cxx);
            IsClang = Path.GetFileName(cxx).Contains("clang", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            if (compiler != "gcc") cxx = FindClang();
            IsClang = cxx != null;
            if (cxx == null && compiler != "clang")
                cxx = UnixPlatform.FindOnPath(_cross && HostPlatform.IsLinux ? Triple + "-g++" : "g++");
            if (cxx == null)
                throw new FileNotFoundException(compiler == "clang" ? "clang++ was not found on PATH. Install clang or set linux.cxx."
                    : _cross ? $"No compiler for {Triple}: install clang (with linux.sysroot) or {Triple}-g++." : "Neither clang++ nor g++ was found on PATH. Install clang or g++.");
        }
        if (_cross && !IsClang && !Path.GetFileName(cxx).StartsWith(Triple, StringComparison.Ordinal))
            throw new InvalidOperationException($"GCC cross compilation needs {Triple}-g++ (set linux.cxx) or Clang.");
        Compiler = cxx;
        Archiver = FindArchiver(settings.Ar, cxx, IsClang);
        _linker = settings.Linker.Trim();
        Log.TraceVerbose($"Linux toolchain: {Compiler}, {Archiver}");
    }

    private static string? FindClang()
    {
        if (UnixPlatform.FindOnPath("clang++") is { } plain) return plain;
        // Distributions without the unversioned link: the newest clang++-N on PATH.
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "clang++-*"))
            .Select(p => (Path: p, Version: int.TryParse(Path.GetFileName(p)["clang++-".Length..], out int v) ? v : -1))
            .Where(c => c.Version > 0).OrderByDescending(c => c.Version).Select(c => c.Path).FirstOrDefault();
    }

    private static string FindArchiver(string configured, string compiler, bool clang)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return UnixPlatform.FindOnPath(Environment.ExpandEnvironmentVariables(configured)) ?? throw new FileNotFoundException("linux.ar was not found.", configured);
        string name = Path.GetFileName(compiler);
        string directory = Path.GetDirectoryName(compiler)!;
        // clang++-18 -> llvm-ar-18; aarch64-linux-gnu-g++ -> aarch64-linux-gnu-gcc-ar.
        var candidates = clang
            ? new[] { "llvm-ar" + (name.StartsWith("clang++-") ? name["clang++".Length..] : ""), "llvm-ar" }
            : new[] { name.EndsWith("g++") ? name[..^3] + "gcc-ar" : "gcc-ar", "gcc-ar" };
        foreach (string candidate in candidates.Append("ar"))
            if (File.Exists(Path.Combine(directory, candidate))) return Path.Combine(directory, candidate);
            else if (UnixPlatform.FindOnPath(candidate) is { } found) return found;
        throw new FileNotFoundException("No archiver (llvm-ar, gcc-ar or ar) was found on PATH. Install binutils.");
    }

    protected override IEnumerable<string> PlatformDefinitions => ["PLATFORM_LINUX"];

    protected override IEnumerable<string> TargetArguments
    {
        get
        {
            if (IsClang && (_cross || _sysroot.Length > 0)) yield return "--target=" + Triple;
            if (_sysroot.Length > 0) yield return "--sysroot=\"" + _sysroot + "\"";
        }
    }

    public override void SetupLinkEnvironment(LinkEnvironment environment)
    {
        environment.InputLibraries.Add("-pthread");
        environment.InputLibraries.Add("dl");
        environment.InputLibraries.Add("m");
    }

    protected override IEnumerable<string> PlatformLinkArguments(LinkEnvironment environment, string output)
    {
        var args = new List<string> { "-Wl,--gc-sections", "-Wl,--build-id=sha1", "-Wl,-z,relro,-z,now" };
        // Single quotes are not needed: arguments are passed to the compiler without a shell.
        args.Add("-Wl,-rpath,$ORIGIN:$ORIGIN/../lib");
        args.Add("-Wl,--enable-new-dtags");
        if (environment.Output == LinkerOutput.SharedLibrary)
        {
            args.Add("-Wl,-soname," + Path.GetFileName(output));
            // Sanitizer runtimes are provided by the executable, so their symbols are undefined here.
            if (environment.Sanitizers == Sanitizer.None) args.Add("-Wl,--no-undefined");
        }
        if (environment.StaticRuntime) args.AddRange(["-static-libstdc++", "-static-libgcc"]);
        string linker = _linker.Length > 0 ? _linker
            : IsClang && environment.LinkTimeCodeGeneration && UnixPlatform.FindOnPath("ld.lld") != null ? "lld" : "";
        if (IsClang && environment.LinkTimeCodeGeneration && linker.Length == 0)
            throw new InvalidOperationException("Clang ThinLTO on Linux needs lld (ld.lld on PATH) or linux.linker = \"gold\" with the LLVM gold plugin.");
        if (linker.Length > 0) args.Add("-fuse-ld=" + linker);
        return args;
    }

    protected override IEnumerable<string> GroupLibraries(IEnumerable<string> files) =>
        files.Prepend("-Wl,--start-group").Append("-Wl,--end-group");
}
