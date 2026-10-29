using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Beast.BuildTool;

/// <summary>Native results of one ABI of an Android application target, ready for packaging.</summary>
internal sealed record AndroidAbiOutput(AndroidPlatform Platform, string Target, string NativeLibrary, AndroidPackagePlan Package,
    IReadOnlyList<string> SharedDependencies, bool StaticRuntime, Sanitizer Sanitizers);

/// <summary>
/// Packs every built ABI into one signed APK: stages native libraries into the Gradle project's
/// jniLibs, configures Gradle from Config/BuildConfig.json through an init script (no edits of the
/// user's build.gradle), and skips Gradle and signing when none of their inputs changed.
/// </summary>
internal static class AndroidPackager
{
    private static readonly string[] KnownAbis = ["arm64-v8a", "armeabi-v7a", "x86", "x86_64"];

    public static async Task PackageAsync(string projectPath, string projectName, TargetConfiguration configuration, IReadOnlyList<AndroidAbiOutput> abis)
    {
        if (abis.Count == 0) return;
        var android = BuildConfigManager.Config.Android;
        // projectName is the application target: the project's main app or another Application target.
        string androidProject = AndroidApplication.GradleProject(projectPath, projectName);
        string jniRoot = Path.Combine(androidProject, "app", "src", "main", "jniLibs");
        string state = AndroidApplication.StateDirectory(projectPath, projectName, configuration);
        Directory.CreateDirectory(state);

        // jniLibs is managed by Beast: an ABI that is no longer built must not stay in the APK.
        var built = abis.Select(a => a.Platform.Abi).ToHashSet(StringComparer.Ordinal);
        foreach (string abi in KnownAbis.Where(a => !built.Contains(a)))
            if (Directory.Exists(Path.Combine(jniRoot, abi))) Directory.Delete(Path.Combine(jniRoot, abi), true);

        var deployedByAbi = new Dictionary<string, List<string>>();
        string resources = Path.Combine(state, "Resources");
        if (Directory.Exists(resources)) Directory.Delete(resources, true);
        bool addressSanitizer = false;
        foreach (var output in abis)
        {
            var platform = output.Platform;
            if (!File.Exists(output.NativeLibrary)) throw new FileNotFoundException($"Android native library ({platform.Abi}) was not produced.", output.NativeLibrary);
            output.Package.Prepare();
            string jniDirectory = Path.Combine(jniRoot, platform.Abi);
            Directory.CreateDirectory(jniDirectory);
            string library = Path.Combine(jniDirectory, "lib" + projectName + ".so");
            File.Copy(output.NativeLibrary, library, true);
            var deployed = new List<string> { library };
            var managed = new List<string>(); // extra files Beast put here, to remove when no longer needed

            // clang++ links against libc++_shared by default. NativeActivity loads the project library
            // directly, so the C++ runtime is packaged beside it unless the target links it statically.
            if (!output.StaticRuntime)
            {
                string runtime = Path.Combine(platform.ToolchainRoot, "sysroot", "usr", "lib", platform.Triple, "libc++_shared.so");
                if (!File.Exists(runtime)) throw new FileNotFoundException("Android NDK C++ shared runtime was not found.", runtime);
                managed.Add(Copy(runtime, jniDirectory, deployed));
            }
            deployed.AddRange(output.Package.NativeFiles.Select(output.Package.DeployNative));
            // Shared library targets this application depends on (transitively), loaded through DT_NEEDED.
            foreach (string dependency in output.SharedDependencies)
            {
                if (!File.Exists(dependency)) throw new FileNotFoundException("Dependency shared library was not produced.", dependency);
                managed.Add(Copy(dependency, jniDirectory, deployed));
            }
            if (output.Sanitizers.HasFlag(Sanitizer.Address))
            {
                addressSanitizer = true;
                managed.Add(Copy(AddressSanitizerRuntime(platform), jniDirectory, deployed));
                // wrap.sh preloads the ASan runtime before the app process starts (debuggable builds only).
                string wrap = Path.Combine(resources, "lib", platform.Abi, "wrap.sh");
                Directory.CreateDirectory(Path.GetDirectoryName(wrap)!);
                File.WriteAllText(wrap, WrapScript, new UTF8Encoding(false));
            }
            RemoveStale(Path.Combine(state, $"Deployed-{platform.Abi}.txt"), jniDirectory, managed);
            deployedByAbi[platform.Abi] = deployed;
        }
        if (addressSanitizer && configuration != TargetConfiguration.Debug)
            Log.TraceWarning("AddressSanitizer on Android needs a debuggable APK: wrap.sh is ignored outside Debug builds.");

        var symbols = new Dictionary<string, List<object>>();
        if (configuration == TargetConfiguration.Release)
            foreach (var output in abis)
                symbols[output.Platform.Abi] = await StripAsync(projectPath, projectName, configuration, output.Platform, deployedByAbi[output.Platform.Abi]);

        // Gradle configuration from BuildConfig.json, applied in finalizeDsl: the last point before
        // AGP locks the DSL, after the user's build.gradle has been evaluated.
        string initScript = Path.Combine(state, "beast-package.gradle");
        ModuleCompilation.WriteIfChanged(initScript, InitScript(android, abis, resources, addressSanitizer, AndroidApplication.ApplicationId(projectPath, projectName)));
        string localProperties = Path.Combine(androidProject, "local.properties");
        ModuleCompilation.WriteIfChanged(localProperties, $"sdk.dir={Escape(Environment.ExpandEnvironmentVariables(android.SdkRoot))}{Environment.NewLine}ndk.dir={Escape(abis[0].Platform.NdkRoot)}{Environment.NewLine}");

        string apkOutput = AndroidApplication.Apk(projectPath, projectName, configuration);
        string task = configuration == TargetConfiguration.Debug ? "assembleDebug" : "assembleRelease";
        string gradleState = Path.Combine(state, "GradleState.json");
        string fingerprint = GradleFingerprint(androidProject, abis, initScript, resources, task, android, configuration);
        if (File.Exists(apkOutput) && File.Exists(gradleState) &&
            JsonSerializer.Deserialize<GradleRecord>(File.ReadAllText(gradleState)) is { } previous &&
            previous.Inputs == fingerprint && previous.Apk == BuildCacheSession.HashFile(apkOutput))
        {
            Log.TraceInfo($"Android APK is up to date: {apkOutput}");
            return;
        }
        if (File.Exists(gradleState)) File.Delete(gradleState);

        string gradlew = Path.Combine(androidProject, HostPlatform.Script("gradlew"));
        if (!File.Exists(gradlew)) throw new FileNotFoundException("Android Gradle wrapper not found.", gradlew);
        HostPlatform.EnsureExecutable(gradlew);
        Log.TraceInfo($"Packaging Android APK ({string.Join(", ", built)}) with Gradle task '{task}'...");
        var start = new ProcessStartInfo(gradlew) { WorkingDirectory = androidProject, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(task);
        start.ArgumentList.Add("--init-script");
        start.ArgumentList.Add(initScript);
        string javaHome = Environment.ExpandEnvironmentVariables(android.JavaHome);
        if (!string.IsNullOrWhiteSpace(javaHome)) start.Environment["JAVA_HOME"] = javaHome;
        using (var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Gradle."))
        {
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string stdout = await stdoutTask, stderr = await stderrTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Gradle packaging failed:{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
        string apkDirectory = Path.Combine(androidProject, "app", "build", "outputs", "apk", configuration == TargetConfiguration.Debug ? "debug" : "release");
        // Gradle chooses the name (unsigned release, signing config or custom
        // output naming). Never guess it or pick a stale APK from this directory.
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(apkDirectory, "output-metadata.json")));
        var elements = metadata.RootElement.GetProperty("elements");
        if (elements.GetArrayLength() != 1)
            throw new InvalidOperationException("Expected one APK output. Split APK packaging is not supported yet.");
        string fileName = elements[0].GetProperty("outputFile").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gradle returned an invalid APK output filename.");
        string apk = Path.Combine(apkDirectory, fileName);
        if (!File.Exists(apk)) throw new FileNotFoundException("Gradle APK output is missing.", apk);
        Directory.CreateDirectory(Path.GetDirectoryName(apkOutput)!);
        await AndroidApkSigner.PublishAsync(projectPath, android, configuration, apk, apkOutput);
        foreach (var (abi, records) in symbols)
            File.WriteAllText(Path.Combine(SymbolsDirectory(projectPath, projectName, configuration, abi), "manifest.json"), JsonSerializer.Serialize(new
            {
                apkSha256 = BuildCacheSession.HashFile(apkOutput), abi, ndk = Path.GetFileName(Path.TrimEndingDirectorySeparator(abis[0].Platform.NdkRoot)),
                libraries = records
            }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(gradleState, JsonSerializer.Serialize(new GradleRecord(fingerprint, BuildCacheSession.HashFile(apkOutput))));
        Log.TraceInfo($"Android APK: {apkOutput}");
    }

    private sealed record GradleRecord(string Inputs, string Apk);

    private static string Copy(string source, string directory, List<string> deployed)
    {
        string destination = Path.Combine(directory, Path.GetFileName(source));
        if (!File.Exists(destination) || BuildCacheSession.HashFile(source) != BuildCacheSession.HashFile(destination)) File.Copy(source, destination, true);
        deployed.Add(destination);
        return Path.GetFileName(destination);
    }

    /// <summary>Deletes files Beast deployed into jniLibs earlier but no longer needs (dropped dependency, ASan off).</summary>
    private static void RemoveStale(string list, string directory, IReadOnlyCollection<string> current)
    {
        var keep = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(list))
            foreach (string stale in File.ReadAllLines(list).Where(n => n.Length > 0 && !keep.Contains(n) && Path.GetFileName(n) == n))
                if (File.Exists(Path.Combine(directory, stale))) File.Delete(Path.Combine(directory, stale));
        File.WriteAllLines(list, keep.Order(StringComparer.OrdinalIgnoreCase));
    }

    private static string AddressSanitizerRuntime(AndroidPlatform platform)
    {
        string arch = platform.Triple.Split('-')[0];
        string name = $"libclang_rt.asan-{arch}-android.so";
        foreach (string root in new[] { "lib", "lib64" }.Select(d => Path.Combine(platform.ToolchainRoot, d, "clang")).Where(Directory.Exists))
            if (Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault() is { } found) return found;
        throw new FileNotFoundException($"NDK AddressSanitizer runtime {name} was not found under {platform.ToolchainRoot}.");
    }

    // From the NDK documentation for ASan with wrap.sh.
    private const string WrapScript = """
        #!/system/bin/sh
        HERE="$(cd "$(dirname "$0")" && pwd)"
        export ASAN_OPTIONS=log_to_syslog=false,allow_user_segv_handler=1
        ASAN_LIB=$(ls "$HERE"/libclang_rt.asan-*-android.so)
        if [ -f "$HERE/libc++_shared.so" ]; then
            export LD_PRELOAD="$ASAN_LIB $HERE/libc++_shared.so"
        else
            export LD_PRELOAD="$ASAN_LIB"
        fi
        "$@"

        """;

    // The main application keeps Symbols/<abi>; other application targets get Symbols/<target>/<abi>.
    private static string SymbolsDirectory(string projectPath, string application, TargetConfiguration configuration, string abi) =>
        AndroidApplication.IsMain(projectPath, application)
            ? Path.Combine(projectPath, "Binaries", "Android", configuration.ToString(), "Symbols", abi)
            : Path.Combine(projectPath, "Binaries", "Android", configuration.ToString(), "Symbols", application, abi);

    private static async Task<List<object>> StripAsync(string projectPath, string application, TargetConfiguration configuration, AndroidPlatform platform, List<string> deployed)
    {
        string strip = platform.Tool("llvm-strip");
        if (!File.Exists(strip)) throw new FileNotFoundException("NDK llvm-strip is required for Release packaging.", strip);
        string symbols = SymbolsDirectory(projectPath, application, configuration, platform.Abi);
        Directory.CreateDirectory(symbols);
        var records = new List<object>();
        foreach (string file in deployed)
        {
            string name = Path.GetFileName(file);
            string original = Path.Combine(symbols, name);
            File.Copy(file, original, true);
            var info = new ProcessStartInfo(strip) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--strip-unneeded");
            info.ArgumentList.Add(file);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start llvm-strip.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Stripping {name} failed: {await stdout}{await stderr}");
            records.Add(new { library = name, originalSha256 = BuildCacheSession.HashFile(original), packagedSha256 = BuildCacheSession.HashFile(file) });
            Log.TraceVerbose($"Release {platform.Abi}/{name}: {new FileInfo(original).Length:N0} -> {new FileInfo(file).Length:N0} bytes; original preserved in Symbols.");
        }
        return records;
    }

    internal static string InitScript(AndroidBuildConfig android, IReadOnlyList<AndroidAbiOutput> abis, string resources, bool legacyPackaging, string applicationId)
    {
        // Single-quoted Groovy strings prevent $ interpolation in project paths.
        static string Quote(string value) => "'" + value.Replace("\\", "/").Replace("'", "\\'") + "'";
        var source = new StringBuilder();
        source.AppendLine($"        main.assets.srcDir({Quote(abis[0].Package.AssetsDirectory)})");
        // AndroidJavaSources: the same for every ABI.
        source.AppendLine($"        main.java.srcDir({Quote(abis[0].Package.JavaDirectory)})");
        foreach (var output in abis) source.AppendLine($"        main.jniLibs.srcDir({Quote(output.Package.NativeDirectory)})");
        if (legacyPackaging) source.AppendLine($"        main.resources.srcDir({Quote(resources)})");
        var dsl = new StringBuilder();
        int compileSdk = android.CompileSdk > 0 ? android.CompileSdk : android.TargetSdk;
        if (compileSdk > 0) dsl.AppendLine($"            a.compileSdk = {compileSdk}");
        if (android.MinSdk > 0) dsl.AppendLine($"            a.defaultConfig.minSdk = {android.MinSdk}");
        if (android.TargetSdk > 0) dsl.AppendLine($"            a.defaultConfig.targetSdk = {android.TargetSdk}");
        dsl.AppendLine($"            a.defaultConfig.applicationId = {Quote(applicationId)}");
        if (android.VersionCode > 0) dsl.AppendLine($"            a.defaultConfig.versionCode = {android.VersionCode}");
        if (!string.IsNullOrWhiteSpace(android.VersionName)) dsl.AppendLine($"            a.defaultConfig.versionName = {Quote(android.VersionName)}");
        dsl.AppendLine("            a.defaultConfig.ndk.abiFilters.clear()");
        dsl.AppendLine($"            a.defaultConfig.ndk.abiFilters.addAll([{string.Join(", ", abis.Select(o => Quote(o.Platform.Abi)))}])");
        // wrap.sh requires extracted native libraries.
        if (legacyPackaging) dsl.AppendLine("            a.packaging.jniLibs.useLegacyPackaging = true");
        return "// Generated by Beast from Config/BuildConfig.json; do not edit.\n" +
            "gradle.afterProject { p, state ->\n" +
            "    if (p.path == ':app' && p.plugins.hasPlugin('com.android.application')) {\n" +
            "        def main = p.extensions.getByName('android').sourceSets.getByName('main')\n" +
            source +
            "    }\n" +
            "}\n" +
            "gradle.beforeProject { p ->\n" +
            "    if (p.path != ':app') return\n" +
            "    p.pluginManager.withPlugin('com.android.application') {\n" +
            "        p.extensions.getByName('androidComponents').finalizeDsl { a ->\n" +
            dsl +
            "        }\n" +
            "    }\n" +
            "}\n";
    }

    /// <summary>Everything Gradle and signing read: the Gradle project, staged inputs, settings and tools.</summary>
    private static string GradleFingerprint(string androidProject, IReadOnlyList<AndroidAbiOutput> abis, string initScript, string resources,
        string task, AndroidBuildConfig android, TargetConfiguration configuration)
    {
        static bool Generated(string relative)
        {
            string first = relative.Split(Path.DirectorySeparatorChar)[0];
            string normalized = relative.Replace('\\', '/');
            return first is ".gradle" or "build" or ".idea" or ".cxx" || normalized.StartsWith("app/build/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("app/.cxx/", StringComparison.OrdinalIgnoreCase);
        }
        IEnumerable<string> Files(string directory, Func<string, bool>? skip = null) => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(f => skip == null || !skip(Path.GetRelativePath(directory, f)))
            : [];
        var files = Files(androidProject, Generated)
            .Concat(abis.SelectMany(a => Files(a.Package.AssetsDirectory).Concat(Files(a.Package.NativeDirectory)).Concat(Files(a.Package.JavaDirectory))))
            .Concat(Files(resources)).Append(initScript)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => f + ":" + BuildCacheSession.HashFile(f));
        var signing = configuration == TargetConfiguration.Release ? android.ReleaseSigning : null;
        string settings = JsonSerializer.Serialize(new
        {
            task, java = Environment.ExpandEnvironmentVariables(android.JavaHome), sdk = Environment.ExpandEnvironmentVariables(android.SdkRoot),
            signing, keyStore = signing != null && File.Exists(signing.KeyStore) ? BuildCacheSession.HashFile(signing.KeyStore) : null,
            tool = BuildCacheSession.HashFile(typeof(AndroidPackager).Assembly.Location)
        });
        return BuildCacheSession.HashText(settings + "\n" + string.Join('\n', files));
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\");
}
