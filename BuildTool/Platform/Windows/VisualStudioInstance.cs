using Microsoft.VisualStudio.Setup.Configuration;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Beast.BuildTool
{
    public enum VisualStudioVersion
    {
        VisualStudio2019,
        VisualStudio2022,
        VisualStudio2026,
    }

    /// <summary>
    /// The compilation optimization hint.
    /// </summary>

    public sealed class VisualStudioInstance : IComparable<VisualStudioInstance>
    {
        private static List<VisualStudioInstance>? _installDirs;

        /// <summary>
        /// The version.
        /// </summary>
        public VisualStudioVersion CurrentVersion;

        public string? Location;
        public static string ? VCToolChainPath;
        /// <summary>
        /// Determines whether any version of the IDE is installed.
        /// </summary>
        /// <returns><c>true</c> if any version is installed; otherwise, <c>false</c>.</returns>
        public static bool HasIDE()
        {
            return GetInstances().Count != 0;
        }


        /// <summary>
        /// Determines whether the specified version of the IDE is installed.
        /// </summary>
        /// <param name="version">The version to check.</param>
        /// <returns><c>true</c> if the specified version is installed; otherwise, <c>false</c>.</returns>
        public static bool HasIDE(VisualStudioVersion version)
        {
            return GetInstances().Any(x => x.CurrentVersion == version);
        }



        private static Dictionary<VisualStudioVersion, string> GetVSToolChains()
        {

            var VSInstances = GetInstances();

            if (VSInstances == null || VSInstances.Count == 0)
            {
                return new Dictionary<VisualStudioVersion, string>();
            }
            Dictionary<VisualStudioVersion, string> Toolsets = new Dictionary<VisualStudioVersion, string>();

            // Visual Studio 2017-2022 - multiple instances
            foreach (var vs in VSInstances)
            {
                if (string.IsNullOrEmpty(vs.Location))
                    continue;

                string rootDir = Path.Combine(vs.Location, "VC", "Tools", "MSVC");

                if (!Directory.Exists(rootDir))
                    continue;

                var toolsets = Directory.GetDirectories(rootDir);
                foreach (var toolset in toolsets)
                {
                    if (Version.TryParse(Path.GetFileName(toolset), out var version) &&
                        (File.Exists(Path.Combine(toolset, "bin", "Hostx64", "x64", "cl.exe")) ||
                         File.Exists(Path.Combine(toolset, "bin", "Hostx86", "x64", "cl.exe"))))
                    {
                        if (version.Major == 14 && version.Minor / 10 == 2)
                            Toolsets[VisualStudioVersion.VisualStudio2019] = toolset;
                        else if (version.Major == 14 && version.Minor / 10 == 3)
                            Toolsets[VisualStudioVersion.VisualStudio2022] = toolset;
                        else if (version.Major == 14 && version.Minor / 10 == 5)
                            Toolsets[VisualStudioVersion.VisualStudio2026] = toolset;
                        else
                            Log.TraceVerbose("Skipping MSVC toolset version not selected by this BuildTool: " + version);
                    }
                }
            }

            foreach (var e in Toolsets)
                Log.TraceVerbose(string.Format("Found Windows toolset {0} at {1}", e.Key, e.Value));


            return Toolsets;
        }
        public static string GetVCToolPath(VisualStudioVersion VSVersion)
        {
            Dictionary<VisualStudioVersion, string> VSToolChains = GetVSToolChains();

            if (VSToolChains == null || VSToolChains.Count == 0)
            {
                throw new Exception("VCTool Path not found!");
            }

            string VCToolChainDir = VSToolChains[VSVersion];
            string CompilerPath = Path.Combine(VCToolChainDir, "bin", "Hostx64", "X64", "cl.exe");
            if (File.Exists(CompilerPath))
                return VCToolChainDir;

            string CompilerPath86 = Path.Combine(VCToolChainDir, "bin", "Hostx86", "x64", "cl.exe");
            if (File.Exists(CompilerPath86))
                return VCToolChainDir;

            return string.Empty;
        }
        public static void SetVCToolPath(VisualStudioVersion VSVersion)
        {
            VCToolChainPath = GetVCToolPath(VSVersion);
            //Environment.SetEnvironmentVariable("VCToolsInstallDir", VCToolChainDir + Path.DirectorySeparatorChar);
            //Log.TraceVerbose("Set VCToolsInstallDir to " + VCToolChainDir + Path.DirectorySeparatorChar);
        }
        public static void EnsureVCToolPath(VisualStudioVersion preferredVersion = VisualStudioVersion.VisualStudio2026)
        {
            if (!string.IsNullOrEmpty(VCToolChainPath))
                return;

            var toolchains = GetVSToolChains();
            if (toolchains == null || toolchains.Count == 0)
                return;

            VisualStudioVersion[] order = new[] { preferredVersion, VisualStudioVersion.VisualStudio2022, VisualStudioVersion.VisualStudio2019 };
            foreach (var v in order)
            {
                if (toolchains.ContainsKey(v))
                {
                    string path = GetVCToolPath(v);
                    if (!string.IsNullOrEmpty(path))
                    {
                        VCToolChainPath = path;
                        return;
                    }
                }
            }

            foreach (var kvp in toolchains)
            {
                string path = GetVCToolPath(kvp.Key);
                if (!string.IsNullOrEmpty(path))
                {
                    VCToolChainPath = path;
                    return;
                }
            }
        }
        public static IReadOnlyList<VisualStudioInstance> GetInstances()
        {
            if (_installDirs != null)
                return _installDirs;
            _installDirs = new List<VisualStudioInstance>();

            // Skip if running on non-Windows system
            //if (Platform.BuildTargetPlatform != TargetPlatform.Windows)
            //return _installDirs;

            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // Visual Studio 2017-2022
                List<VisualStudioInstance> ?preReleaseInstallDirs = null;
                try
                {
                    SetupConfiguration setup = new SetupConfiguration();
                    IEnumSetupInstances enumerator = setup.EnumAllInstances();

                    ISetupInstance[] instances = new ISetupInstance[1];
                    while (true)
                    {
                        enumerator.Next(1, instances, out int fetchedCount);
                        if (fetchedCount == 0)
                            break;

                        ISetupInstance2 instance = (ISetupInstance2)instances[0];
                        if ((instance.GetState() & InstanceState.Local) == InstanceState.Local)
                        {
                            VisualStudioVersion version;
                            string displayName = instance.GetDisplayName();
                            if (displayName.Contains("2019"))
                                version = VisualStudioVersion.VisualStudio2019;
                            else if (displayName.Contains("2022"))
                                version = VisualStudioVersion.VisualStudio2022;
                            else if (displayName.Contains("2026"))
                                version = VisualStudioVersion.VisualStudio2026;
                            else
                            {
                                Log.TraceWarning(string.Format("Unknown Visual Studio installation. Display name: {0}", displayName));
                                continue;
                            }

                            var vsInstance = new VisualStudioInstance
                            {
                                CurrentVersion = version,
                                Location = instance.GetInstallationPath(),
                            };

                            if (instance is ISetupInstanceCatalog catalog && catalog.IsPrerelease())
                            {
                                if (preReleaseInstallDirs == null)
                                    preReleaseInstallDirs = new List<VisualStudioInstance>();
                                preReleaseInstallDirs.Add(vsInstance);
                            }
                            else
                            {
                                _installDirs.Add(vsInstance);
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore errors
                }

                // Add pre-release locations after the normal installations
                if (preReleaseInstallDirs != null)
                    _installDirs.AddRange(preReleaseInstallDirs);
            }

            // Sort from the latest to the oldest
            _installDirs.Sort();

            foreach (var e in _installDirs)
                Log.TraceVerbose($"Found {e.CurrentVersion} at {e.Location}");

            return _installDirs;
        }

        /// <inheritdoc />
        public int CompareTo(VisualStudioInstance? other)
        {
            if (other == null || Location == null)
                return 1;

            if (CurrentVersion == other.CurrentVersion)
                return Location.CompareTo(other.Location);

            return (int)CurrentVersion < (int)other.CurrentVersion ? 1 : -1;
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            if (Location == null)
                return -1;

            return Location.GetHashCode();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{CurrentVersion} at {Location}";
        }
    }

}
