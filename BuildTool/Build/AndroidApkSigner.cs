using System.Diagnostics;

namespace Beast.BuildTool;

/// <summary>Publish only verified, installable APKs; never put passwords in command arguments.</summary>
internal static class AndroidApkSigner
{
    public static async Task PublishAsync(string projectPath, AndroidBuildConfig android,
        TargetConfiguration configuration, string input, string output)
    {
        string javaBin = Path.Combine(AndroidToolchain.RequireJavaHome(), "bin");
        string java = Path.Combine(javaBin, HostPlatform.Executable("java"));
        string toolsRoot = Path.Combine(AndroidToolchain.RequireSdkRoot(), "build-tools");
        string? signer = Directory.Exists(toolsRoot) ? Directory.GetDirectories(toolsRoot)
            .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var v) ? v : null))
            .Where(x => x.Version != null).OrderByDescending(x => x.Version)
            .Select(x => Path.Combine(x.Path, "lib", "apksigner.jar")).FirstOrDefault(File.Exists) : null;
        if (!File.Exists(java) || signer == null)
            throw new FileNotFoundException("APK signing requires Java (android.javaHome or JAVA_HOME) and Android SDK build-tools/apksigner.jar.");
        var signing = configuration == TargetConfiguration.Release ? android.ReleaseSigning : new AndroidSigningConfig();
        if (signing == null) throw new InvalidOperationException("android.releaseSigning must be an object, not null.");
        bool custom = !string.IsNullOrWhiteSpace(signing.KeyStore);
        if (!custom && new[] { signing.KeyAlias, signing.StorePasswordEnv, signing.KeyPasswordEnv }.Any(x => !string.IsNullOrWhiteSpace(x)))
            throw new InvalidOperationException("android.releaseSigning.keyStore is required when other signing fields are set.");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary = Path.Combine(Path.GetDirectoryName(output)!, $".beast-signing-{Guid.NewGuid():N}.apk");
        try
        {
            if (!custom && await RunAsync(java, ["-jar", signer, "verify", input]) == 0)
            {
                // Existing Gradle signing belongs to the project. Do not replace its identity.
                File.Copy(input, temporary);
                Log.TraceInfo("APK already signed by Gradle; preserving its signature.");
            }
            else
            {
                string keyStore, alias, storePassword, keyPassword;
                if (custom)
                {
                    keyStore = Path.GetFullPath(Environment.ExpandEnvironmentVariables(signing.KeyStore), projectPath);
                    if (!File.Exists(keyStore)) throw new FileNotFoundException("Configured Android signing keystore not found.", keyStore);
                    alias = signing.KeyAlias;
                    if (string.IsNullOrWhiteSpace(alias)) throw new InvalidOperationException("android.releaseSigning.keyAlias is required.");
                    storePassword = ReadPassword(signing.StorePasswordEnv);
                    keyPassword = string.IsNullOrWhiteSpace(signing.KeyPasswordEnv) ? storePassword : ReadPassword(signing.KeyPasswordEnv);
                }
                else
                {
                    string userAndroid = Environment.GetEnvironmentVariable("ANDROID_USER_HOME")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android");
                    if (string.IsNullOrWhiteSpace(userAndroid)) throw new InvalidOperationException("ANDROID_USER_HOME must not be empty.");
                    keyStore = Path.GetFullPath(Path.Combine(userAndroid, "debug.keystore"));
                    alias = "androiddebugkey"; storePassword = keyPassword = "android";
                    if (!File.Exists(keyStore))
                        await CreateDebugKeyAsync(Path.Combine(javaBin, HostPlatform.Executable("keytool")), keyStore);
                    Log.TraceWarning("APK uses the local Android DEBUG key for installation/testing. Configure android.releaseSigning for publication.");
                }
                var secrets = PasswordEnvironment(storePassword, keyPassword);
                int signed = await RunAsync(java, ["-jar", signer, "sign", "--ks", keyStore, "--ks-key-alias", alias,
                    "--ks-pass", "env:BEAST_APK_STORE_PASSWORD", "--key-pass", "env:BEAST_APK_KEY_PASSWORD",
                    "--v4-signing-enabled", "false", "--out", temporary, input], secrets);
                if (signed != 0) throw new InvalidOperationException("APK signing failed. Check the keystore, alias and password environment variables; existing output was not replaced.");
            }
            if (await RunAsync(java, ["-jar", signer, "verify", temporary]) != 0)
                throw new InvalidOperationException("APK signature verification failed; existing output was not replaced.");
            File.Move(temporary, output, true);
            Log.TraceInfo("APK signature verified.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ReadPassword(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A signing password environment variable name is required.");
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Signing password environment variable '{name}' is not set.");
    }

    private static Dictionary<string, string> PasswordEnvironment(string store, string key) => new()
    { ["BEAST_APK_STORE_PASSWORD"] = store, ["BEAST_APK_KEY_PASSWORD"] = key };

    private static async Task CreateDebugKeyAsync(string keytool, string keyStore)
    {
        if (!File.Exists(keytool)) throw new FileNotFoundException("keytool.exe is required to create the local Android debug key.", keytool);
        Directory.CreateDirectory(Path.GetDirectoryName(keyStore)!);
        string temporary = keyStore + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (await RunAsync(keytool, ["-genkeypair", "-noprompt", "-keystore", temporary, "-storetype", "JKS",
                "-alias", "androiddebugkey", "-storepass:env", "BEAST_APK_STORE_PASSWORD", "-keypass:env", "BEAST_APK_KEY_PASSWORD",
                "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000", "-dname", "CN=Android Debug,O=Android,C=US"],
                PasswordEnvironment("android", "android")) != 0)
                throw new InvalidOperationException("Failed to create the local Android debug signing key.");
            // Another simultaneous build may have created it. Never rotate/overwrite a key.
            try { File.Move(temporary, keyStore, false); }
            catch (IOException) when (File.Exists(keyStore)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<int> RunAsync(string executable, IEnumerable<string> arguments, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (environment != null) foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Android signing tool.");
        process.StandardInput.Close(); // Never wait for an interactive password prompt.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await stdout; await stderr;
        // Do not echo tool output: it may contain sensitive signing inputs.
        return process.ExitCode;
    }
}
