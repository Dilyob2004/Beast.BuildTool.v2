using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Beast.BuildTool
{

    public static class GlobalEnvironment
    {

        internal static string GetExecutingAssemblyLocation()
        {
            return new Uri(Assembly.GetExecutingAssembly().Location).LocalPath;
        }
        internal static string GetExecutingAssemblyLocationDirectory()
        {
            string path = GetExecutingAssemblyLocation();
            string? directoryName = Path.GetDirectoryName(path);

            if (directoryName == null)
            {
                return Environment.CurrentDirectory;
            }

            return directoryName;
        }
        public static readonly string BuildFilesPostfix = ".Build.cs";
        public static readonly string WorkingDirectory = GetExecutingAssemblyLocationDirectory();
        public static string WorkspacePath => ProjectPath ?? Environment.CurrentDirectory;

        public static string BinaryPath => Path.Combine(WorkspacePath, "Binaries");
        public static string IntermediatePath => Path.Combine(WorkspacePath, "Intermediate");
        public static string ProjectsPath => Path.Combine(IntermediatePath, "Projects");
        public static string BuildPath => Path.Combine(IntermediatePath, "Build");


        public static string AssembleRulesPath => Path.Combine(IntermediatePath, "Build", "Rules");

        public static string SourcePath => ProjectPath != null ? Path.Combine(ProjectPath, "Source") : Path.Combine(WorkspacePath, "Source");
        public static string ThirdPartyPath => ProjectPath != null ? Path.Combine(ProjectPath, "ThirdParty") : Path.Combine(WorkspacePath, "ThirdParty");
        public static string TemplatesPath => Path.Combine(AppContext.BaseDirectory, "Templates");

        public static string? ProjectPath { get; set; } = null;

        /// <summary>
        /// Root whose Intermediate holds the response and link files of the target being planned: the
        /// built project, or a shared external project (so that it compiles with the same command lines
        /// as when that project builds itself). Planning is sequential; see PlanFor.
        /// </summary>
        public static string? PlanningRoot { get; private set; }
        public static string PlanningIntermediatePath => PlanningRoot != null ? Path.Combine(PlanningRoot, "Intermediate") : IntermediatePath;
        /// <summary>Root that names of planned files are relative to (object file ids): PlanningRoot or the workspace.</summary>
        public static string PlanningWorkspacePath => PlanningRoot ?? WorkspacePath;

        public static IDisposable PlanFor(string root)
        {
            string? previous = PlanningRoot;
            PlanningRoot = root;
            return new Scope(() => PlanningRoot = previous);
        }

        private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
        public static string? ProjectFilePath { get; set; } = null;
        public static string? ProjectName { get; set; } = null;
        public static bool IsProjectBuild => !string.IsNullOrEmpty(ProjectPath);

    }


}
