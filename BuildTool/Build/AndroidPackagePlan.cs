using System.Buffers.Binary;
using System.Text.Json;

namespace Beast.BuildTool;

/// <summary>APK inputs staged outside the user's Gradle source tree.</summary>
public sealed class AndroidPackagePlan
{
    private sealed record Entry(string Source, string Destination, bool Native);
    private readonly List<Entry> entries = [];
    private readonly string root;
    public string AssetsDirectory => Path.Combine(root, "assets");
    public string NativeDirectory => Path.Combine(root, "deploy", "jniLibs");
    public IEnumerable<string> NativeFiles => entries.Where(e => e.Native).Select(e => Path.Combine(root, "inputs", e.Destination));
    public string Abi { get; }
    private readonly ushort elfMachine;

    public AndroidPackagePlan(string buildDirectory, string projectName, IReadOnlyList<ModuleRules> modules, BuildStepPlan paths, AndroidPlatform platform,
        IEnumerable<ModuleRules>? resourceModules = null)
    {
        Abi = platform.Abi;
        elfMachine = platform.ElfMachine;
        var destinations = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            foreach (var item in module.AndroidAssets) Collect(module, item, false);
            foreach (var item in module.AndroidNativeLibraries) Collect(module, item, true);
        }
        // Resources (also of dependency targets) go to assets/ with the same relative paths as on
        // other platforms, so BeastResources.h reads "Assets/x.png" everywhere.
        foreach (var module in resourceModules ?? modules)
            foreach (var item in module.Resources) Collect(module, item, false);
        entries = destinations.Values.OrderBy(e => e.Destination, StringComparer.Ordinal).ToList();
        // A changed file set gets its own staging tree. Removed declarations cannot
        // leak into APKs and no files in the user's Gradle project need deleting.
        string layout = BuildCacheSession.HashText(JsonSerializer.Serialize(entries));
        root = Path.Combine(buildDirectory, "AndroidPackage", layout);

        void Collect(ModuleRules module, RuntimeDependency item, bool native)
        {
            string source = Path.GetFullPath(paths.Expand(module, item.Source), module.ModuleDirectory);
            string destination = (item.Destination ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(source))).Replace('\\', '/');
            ValidateRelative(destination);
            if (native)
            {
                if (destination.Contains('/') || destination.Contains('\\') || !destination.StartsWith("lib", StringComparison.Ordinal) || !destination.EndsWith(".so", StringComparison.Ordinal))
                    throw new InvalidOperationException($"AndroidNativeLibraries destination must be a filename: libName.so ({Abi}).");
                if (destination.Equals("lib" + projectName + ".so", StringComparison.OrdinalIgnoreCase) || destination.Equals("libc++_shared.so", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Reserved Android native library: {destination}");
                Add(source, "jniLibs/" + Abi + "/" + destination, true);
            }
            else if (Directory.Exists(source))
            {
                void Visit(string directory)
                {
                    RejectLink(directory);
                    foreach (string file in Directory.EnumerateFileSystemEntries(directory))
                        if (Directory.Exists(file)) Visit(file);
                        else { RejectLink(file); Add(file, "assets/" + destination + "/" + Path.GetRelativePath(source, file).Replace('\\', '/'), false); }
                }
                Visit(source);
            }
            else Add(source, "assets/" + destination.Replace('\\', '/'), false);
        }
        void Add(string source, string destination, bool native)
        {
            if (destinations.TryGetValue(destination, out var existing))
            {
                if (!source.Equals(existing.Source, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Conflicting APK destination: {destination}");
                return;
            }
            destinations.Add(destination, new(source, destination, native));
        }
    }

    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Split('/', '\\').Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidOperationException($"Invalid APK destination (expected relative path without traversal): {path}");
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"APK input is a symlink/junction: {path}");
    }

    public void AddCopies(CommandExecutor executor)
    {
        foreach (var entry in entries)
        {
            string output = Path.Combine(root, entry.Native ? "inputs" : "", entry.Destination);
            BuildStepPlan.ValidateOutput(root, output);
            BuildStepPlan.AddCopyCommand(executor, entry.Source, output, root, Path.Combine(root, "States"), $"Staging APK {entry.Destination}...");
        }
    }

    public void Prepare()
    {
        foreach (string file in NativeFiles) ValidateLibrary(file, elfMachine, Abi);
        BuildStepPlan.ValidateOutput(root, Path.Combine(AssetsDirectory, ".path-check"));
        BuildStepPlan.ValidateOutput(root, Path.Combine(NativeDirectory, ".path-check"));
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(NativeDirectory);
    }

    public string DeployNative(string input)
    {
        string output = Path.Combine(NativeDirectory, Abi, Path.GetFileName(input));
        BuildStepPlan.ValidateOutput(root, output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (!File.Exists(output) || BuildCacheSession.HashFile(input) != BuildCacheSession.HashFile(output)) File.Copy(input, output, true);
        return output;
    }

    public static void ValidateArm64Library(string file) => ValidateLibrary(file, 183, "arm64-v8a");

    /// <summary>ELF64 little-endian shared object (ET_DYN) for the given e_machine.</summary>
    public static void ValidateLibrary(string file, ushort machine, string abi)
    {
        using var stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[64];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read != 64 || header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F' ||
            header[4] != 2 || header[5] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) != 3 || BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) != machine)
            throw new InvalidOperationException($"Android native library must be an ELF64 little-endian {abi} ({(machine == 183 ? "ARM64" : "x86-64")}) shared object: {file}");
    }
}
