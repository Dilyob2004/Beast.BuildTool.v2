using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace Beast.BuildTool
{
    public class Assembler
    {

        public static string BuildFileName
        {
            get
            {
                return CacheFileName;
            }
        }

        internal static string CacheFileName = "BuilderRules.dll";


        private string ? _cacheFolderPath;

        /// <summary>
        /// The default assembly references added to the projects.
        /// </summary>
        public static readonly Assembly[] DefaultReferences =
        {
            typeof(IntPtr).Assembly, // mscorlib.dll
            typeof(Enumerable).Assembly, // System.Linq.dll
            typeof(ISet<>).Assembly, // System.dll
            typeof(ModuleRules).Assembly // BuildTool.dll
        };

        /// <summary>
        /// The source files for compilation.
        /// </summary>
        public readonly List<string> SourceFiles = new List<string>();

        /// <summary>
        /// The external user assembly references to use while compiling.
        /// </summary>
        public readonly List<Assembly> Assemblies = new List<Assembly>();

        /// <summary>
        /// The external user assembly file names to use while compiling.
        /// </summary>
        public readonly List<string> References = new List<string>();

        /// <summary>
        /// The C# preprocessor symbols to use while compiling.
        /// </summary>
        public readonly List<string> PreprocessorSymbols = new List<string>();

        public Assembler(List<string> sourceFiles, string? cacheFolderPath = null)
        {
            SourceFiles.AddRange(sourceFiles);
            if(cacheFolderPath != null)
                _cacheFolderPath = cacheFolderPath;
        }

        /// <summary>
        /// Builds the assembly.
        /// </summary>
        /// <remarks>Throws an exception in case of any errors.</remarks>
        /// <returns>The created and loaded assembly.</returns>
        // Every evaluation gets its own context: rules assemblies share one file name, and a process
        // (tests, a future long-running host) may load several projects or revisions of them.
        // Beast.BuildTool itself still resolves from the default context, so rule types match.
        private static AssemblyLoadContext NewRulesContext() => new("BeastRules-" + Guid.NewGuid().ToString("N"));

        public Assembly Build()
        {
            DateTime recentWriteTime = DateTime.MinValue;
            string? cacheAssemblyPath = null, cacheInfoPath = null, buildInfo = null;
            if (_cacheFolderPath != null)
            {
                string ruleFileName = GlobalEnvironment.IsProjectBuild ? $"BuilderRules_{GlobalEnvironment.ProjectName}.dll" : CacheFileName;
                string ruleInfoName = GlobalEnvironment.IsProjectBuild ? $"BuilderRulesInfo_{GlobalEnvironment.ProjectName}.txt" : "BuilderRulesInfo.txt";
                cacheAssemblyPath = Path.Combine(_cacheFolderPath, ruleFileName);
                cacheInfoPath = Path.Combine(_cacheFolderPath, ruleInfoName);

                foreach (var sourceFile in SourceFiles)
                {
                    // FIXME: compare and cache individual write times!
                    DateTime lastWriteTime = File.GetLastWriteTime(sourceFile);
                    if (lastWriteTime > recentWriteTime)
                        recentWriteTime = lastWriteTime;
                }

                // Include build tool version (eg. skip using cached assembly after editing build tool)
                var executingAssembly = Assembly.GetExecutingAssembly();
                var executingAssemblyLocation = executingAssembly.Location;
                {
                    DateTime lastWriteTime = File.GetLastWriteTime(executingAssemblyLocation);
                    if (lastWriteTime > recentWriteTime)
                        recentWriteTime = lastWriteTime;
                }

                // Construct current configuration and runtime info to be cached alongside with compiled assembly so the build rules are cached only when using the same .NET/Flax.Build/etc.
                // Content and membership matter, including files added with an older timestamp.
                string fingerprint = string.Join("|", SourceFiles.Order(StringComparer.OrdinalIgnoreCase).Append(executingAssemblyLocation)
                    .Select(path => path + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));
                buildInfo = GlobalEnvironment.WorkspacePath + ';' + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ';' + fingerprint;

                // Check if cache files exist
                if (File.Exists(cacheAssemblyPath) && File.Exists(cacheInfoPath))
                {
                    var lines = File.ReadAllLines(cacheInfoPath);
                    if (lines.Length == 3 &&
                        long.TryParse(lines[0], out var cacheTimeTicks) &&
                        string.Equals(buildInfo, lines[1], StringComparison.Ordinal) &&
                        new HashSet<string>(lines[2].Split(';'), StringComparer.OrdinalIgnoreCase).SetEquals(SourceFiles))
                    {
                        // Cached time and
                        var cacheTime = DateTime.FromBinary(cacheTimeTicks);
                        if (recentWriteTime <= cacheTime)
                        {
                            // use cached assembly
                            using var cachedStream = File.OpenRead(cacheAssemblyPath);
                            Assembly cachedAssembly = NewRulesContext().LoadFromStream(cachedStream);
                            return cachedAssembly;
                        }
                    }
                }
            }

            Stopwatch sw = Stopwatch.StartNew();

            // Collect references
            HashSet<string> references = new HashSet<string>();
            foreach (var defaultReference in DefaultReferences)
                references.Add(defaultReference.Location);
            foreach (var assemblyFile in References)
                references.Add(assemblyFile);
            foreach (var assembly in Assemblies)
                if (!assembly.IsDynamic)
                    references.Add(assembly.Location);

            var DInfo = Directory.GetParent(DefaultReferences[0].Location);

            if(DInfo == null)
                throw new Exception("Failed to get default assembly location.");

            var stdLibPath = DInfo.FullName;
            references.Add(Path.Combine(stdLibPath, "System.dll"));
            references.Add(Path.Combine(stdLibPath, "System.Runtime.dll"));
            references.Add(Path.Combine(stdLibPath, "System.Collections.dll"));
            references.Add(Path.Combine(stdLibPath, "Microsoft.Win32.Registry.dll"));

            // HACK: C# will give compilation errors if a LIB variable contains non-existing directories
            Environment.SetEnvironmentVariable("LIB", null);

            CSharpCompilationOptions defaultCompilationOptions =
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithUsings(new[] { "System", })
                    .WithPlatform(Microsoft.CodeAnalysis.Platform.AnyCpu)
                    .WithOptimizationLevel(OptimizationLevel.Debug);

            List<MetadataReference> defaultReferences = new List<MetadataReference>();
            foreach (var r in references)
                defaultReferences.Add(MetadataReference.CreateFromFile(r));

            // Run the compilation
            var memoryStream = new MemoryStream();
            CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest).WithPreprocessorSymbols(PreprocessorSymbols);
            var syntaxTrees = new List<SyntaxTree>();
            foreach (var sourceFile in SourceFiles)
            {
                var stringText = SourceText.From(File.ReadAllText(sourceFile), Encoding.UTF8);
                var parsedSyntaxTree = SyntaxFactory.ParseSyntaxTree(stringText, parseOptions, sourceFile);
                syntaxTrees.Add(parsedSyntaxTree);
            }

            var compilation = CSharpCompilation.Create(CacheFileName, syntaxTrees.ToArray(), defaultReferences, defaultCompilationOptions);
            EmitResult emitResult = compilation.Emit(memoryStream);

            // Process warnings and errors
            foreach (var diagnostic in emitResult.Diagnostics)
            {
                var msg = diagnostic.ToString();
                if (diagnostic.Severity == DiagnosticSeverity.Warning)
                    Log.TraceWarning(msg);
                else if (diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error)
                    Log.TraceError(msg);
            }
            if (!emitResult.Success)
                throw new Exception("Failed to build assembly.");

            memoryStream.Seek(0, SeekOrigin.Begin);
            Assembly compiledAssembly = NewRulesContext().LoadFromStream(memoryStream);

            if (_cacheFolderPath != null && cacheAssemblyPath != null)
            {
                memoryStream.Seek(0, SeekOrigin.Begin);

                if (!Directory.Exists(_cacheFolderPath))
                    Directory.CreateDirectory(_cacheFolderPath);

                // Save assembly to cache file
                using (FileStream fileStream = File.Open(cacheAssemblyPath, FileMode.Create, FileAccess.Write))
                {
                    memoryStream.CopyTo(fileStream);
                    fileStream.Close();
                }

                // Save build info to cache file
                File.WriteAllLines(cacheInfoPath!, new[]
                {
                    recentWriteTime.ToBinary().ToString(),
                    buildInfo ?? string.Empty,
                    string.Join(';', SourceFiles)
                });
            }

            sw.Stop();
            Log.TraceVerbose("Assembler time: " + sw.Elapsed.TotalSeconds.ToString() + "s");
            return compiledAssembly;
        }
    }

}
