using System.Diagnostics;

namespace Beast.BuildTool;

/// <summary>Starts an Android emulator when needed, installs the APK, and launches the app activity.</summary>
internal static class AndroidRunner
{
    public static async Task RunAsync(
        string projectPath,
        string projectName,
        TargetConfiguration configuration,
        string? requestedAvd,
        string? requestedDevice,
        bool waitForExit)
    {
        string apk = AndroidApplication.Apk(projectPath, projectName, configuration);
        if (!File.Exists(apk))
            throw new FileNotFoundException("Android APK was not found. Build the project first or omit --no-build.", apk);

        // The only connected device or emulator is used without --device; otherwise an AVD is started.
        var (adb, serial) = await ConnectAsync(requestedAvd, requestedDevice);

        Log.TraceInfo($"Installing Android APK on {serial}...");
        await RunCheckedAsync(adb, ["-s", serial, "install", "-r", apk], "APK installation failed");

        // applicationId (on-device package) may differ from the Java namespace of the activity.
        string package = AndroidApplication.Namespace(projectName);
        string packageName = AndroidApplication.ApplicationId(projectPath, projectName);
        string componentName = packageName + "/" + package + ".GameActivity";
        await RunCheckedAsync(adb, ["-s", serial, "shell", "am", "force-stop", packageName], "Could not stop the previous Android app instance");
        await RunCheckedAsync(adb, ["-s", serial, "shell", "am", "start", "-n", componentName], "Could not start the Android app");
        Log.TraceInfo($"Android app started: {componentName} on {serial}.");

        if (waitForExit)
            await WaitForAppExitAsync(adb, serial, packageName);
    }

    /// <summary>adb and the device to use: the requested one, else the only connected device or emulator.</summary>
    internal static async Task<(string Adb, string Serial)> ConnectAsync(string? requestedAvd, string? requestedDevice)
    {
        AndroidBuildConfig android = BuildConfigManager.Config.Android;
        string sdkRoot = AndroidToolchain.SdkRootOrDefault();
        string adb = Path.Combine(sdkRoot, "platform-tools", HostPlatform.Executable("adb"));
        string emulator = Path.Combine(sdkRoot, "emulator", HostPlatform.Executable("emulator"));
        if (!File.Exists(adb)) throw new FileNotFoundException("Android Debug Bridge (adb) was not found.", adb);
        string? device = requestedDevice;
        if (string.IsNullOrWhiteSpace(device))
        {
            var connected = await GetEmulatorDevicesAsync(adb, emulatorsOnly: false);
            if (connected.Count == 1) device = connected[0];
        }
        string serial = await ResolveDeviceAsync(adb, emulator, android, requestedAvd, device);
        await WaitForBootAsync(adb, serial, android.EmulatorBootTimeoutSeconds);
        return (adb, serial);
    }

    /// <summary>Primary ABI of the device (ro.product.cpu.abi), e.g. arm64-v8a or x86_64.</summary>
    internal static async Task<string> DeviceAbiAsync(string adb, string serial)
    {
        var result = await RunProcessAsync(adb, ["-s", serial, "shell", "getprop", "ro.product.cpu.abi"]);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Could not read the ABI of {serial}: {result.Output}");
        return result.Output.Trim();
    }

    /// <summary>
    /// Copies a test executable and its shared libraries to /data/local/tmp/beast/&lt;name&gt; and runs it
    /// there with LD_LIBRARY_PATH set. Output is shown; returns the test's exit code.
    /// </summary>
    internal static async Task<int> RunTestAsync(string adb, string serial, string executable, IEnumerable<string> libraries,
        IEnumerable<string> arguments, int timeoutSeconds)
    {
        string name = Path.GetFileName(executable);
        string directory = "/data/local/tmp/beast/" + name;
        await RunCheckedAsync(adb, ["-s", serial, "shell", "rm", "-rf", directory, "&&", "mkdir", "-p", directory], "Could not prepare the device test directory");
        foreach (string file in libraries.Prepend(executable).Distinct(StringComparer.OrdinalIgnoreCase))
            await RunCheckedAsync(adb, ["-s", serial, "push", file, directory + "/" + Path.GetFileName(file)], $"Could not push {Path.GetFileName(file)}");
        static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
        string command = $"cd {directory} && chmod 755 ./{name} && LD_LIBRARY_PATH={directory} ./{name} {string.Join(' ', arguments.Select(Quote))}; echo BEAST_EXIT=$?";
        var info = new ProcessStartInfo(adb) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "-s", serial, "shell", command }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start adb.");
        int exit = -1;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.StartsWith("BEAST_EXIT=", StringComparison.Ordinal) && int.TryParse(e.Data["BEAST_EXIT=".Length..], out int code)) exit = code;
            else Console.WriteLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await RunProcessAsync(adb, ["-s", serial, "shell", "pkill", "-f", directory + "/" + name]);
            throw new TimeoutException($"{name} timed out after {timeoutSeconds} s on {serial}.");
        }
        process.WaitForExit(); // drain asynchronous output
        if (exit < 0) throw new InvalidOperationException($"{name}: adb did not report the exit code (adb exit {process.ExitCode}).");
        return exit;
    }

    internal static async Task<string> ResolveDeviceAsync(string adb, string emulator, AndroidBuildConfig android, string? requestedAvd, string? requestedDevice)
    {
        if (!string.IsNullOrWhiteSpace(requestedDevice))
        {
            // Any adb device: an emulator or a phone connected over USB/Wi-Fi.
            var connected = await GetEmulatorDevicesAsync(adb, emulatorsOnly: false);
            if (!connected.Contains(requestedDevice, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Android device '{requestedDevice}' is not connected. Connected devices: {FormatDevices(connected)}.");
            return requestedDevice;
        }
        List<string> runningDevices = await GetEmulatorDevicesAsync(adb);

        if (runningDevices.Count == 1)
            return runningDevices[0];
        if (runningDevices.Count > 1)
            throw new InvalidOperationException($"Several Android emulators are connected ({FormatDevices(runningDevices)}). Select one with --device <serial>.");

        List<string> avds = await GetAvdsAsync(emulator);
        string avd = !string.IsNullOrWhiteSpace(requestedAvd) ? requestedAvd : android.DefaultAvd;
        if (string.IsNullOrWhiteSpace(avd))
        {
            if (avds.Count == 1)
                avd = avds[0];
            else
                throw new InvalidOperationException($"Several Android virtual devices are available ({FormatDevices(avds)}). Set android.defaultAvd in Config/BuildConfig.json or pass --avd <name>.");
        }
        if (!avds.Contains(avd, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Android virtual device '{avd}' was not found. Available AVDs: {FormatDevices(avds)}.");

        Log.TraceInfo($"Starting Android emulator '{avd}'...");
        var startInfo = new ProcessStartInfo(emulator)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-avd");
        startInfo.ArgumentList.Add(avd);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Android Emulator.");
        return await WaitForNewEmulatorAsync(adb, android.EmulatorBootTimeoutSeconds);
    }

    private static async Task<List<string>> GetAvdsAsync(string emulator)
    {
        ProcessResult result = await RunProcessAsync(emulator, ["-list-avds"]);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Could not list Android virtual devices:{Environment.NewLine}{result.Output}");
        return result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static async Task<List<string>> GetEmulatorDevicesAsync(string adb, bool emulatorsOnly = true)
    {
        ProcessResult result = await RunProcessAsync(adb, ["devices"]);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Could not query adb devices:{Environment.NewLine}{result.Output}");
        return result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(line => line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            // An emulator reports "offline" while it is still booting. It is still
            // the right target: WaitForBootAsync will wait for it instead of trying
            // to launch a second instance of the same AVD.
            .Where(parts => parts.Length >= 2 && (!emulatorsOnly || parts[0].StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)))
            .Select(parts => parts[0])
            .ToList();
    }

    private static async Task<string> WaitForNewEmulatorAsync(string adb, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(NormalizeTimeout(timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            List<string> devices = await GetEmulatorDevicesAsync(adb);
            if (devices.Count == 1) return devices[0];
            if (devices.Count > 1) throw new InvalidOperationException($"Several Android emulators connected while starting the requested AVD: {FormatDevices(devices)}. Use --device <serial>.");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        throw new TimeoutException("Timed out waiting for Android Emulator to connect to adb.");
    }

    internal static async Task WaitForBootAsync(string adb, string serial, int timeoutSeconds)
    {
        Log.TraceInfo($"Waiting for Android emulator {serial} to finish booting...");
        DateTime deadline = DateTime.UtcNow.AddSeconds(NormalizeTimeout(timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ProcessResult result = await RunProcessAsync(adb, ["-s", serial, "shell", "getprop", "sys.boot_completed"]);
            if (result.ExitCode == 0 && result.Output.Trim() == "1") return;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        throw new TimeoutException($"Timed out waiting for Android emulator {serial} to boot.");
    }

    private static async Task WaitForAppExitAsync(string adb, string serial, string packageName)
    {
        Log.TraceInfo("Android app is running. Close the app or emulator to finish this launch session.");
        while (true)
        {
            ProcessResult result = await RunProcessAsync(adb, ["-s", serial, "shell", "pidof", packageName]);
            if (result.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(result.Output) &&
                    !result.Output.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    Log.TraceInfo($"Android launch session ended: {result.Output.Trim()}");
                else
                    Log.TraceInfo("Android app was closed.");
                return;
            }

            if (string.IsNullOrWhiteSpace(result.Output))
            {
                Log.TraceInfo("Android app was closed.");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task RunCheckedAsync(string executable, IEnumerable<string> arguments, string errorMessage)
    {
        ProcessResult result = await RunProcessAsync(executable, arguments);
        if (result.ExitCode != 0) throw new InvalidOperationException($"{errorMessage}:{Environment.NewLine}{result.Output}");
        if (!string.IsNullOrWhiteSpace(result.Output)) Log.TraceVerbose(result.Output.Trim());
    }

    private static async Task<ProcessResult> RunProcessAsync(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{executable}'.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, (await outputTask) + (await errorTask));
    }

    private static int NormalizeTimeout(int seconds) => Math.Clamp(seconds, 30, 600);
    private static string FormatDevices(IEnumerable<string> values) => string.Join(", ", values);

    private readonly record struct ProcessResult(int ExitCode, string Output);
}
