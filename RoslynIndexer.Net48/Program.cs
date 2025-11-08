using Microsoft.Build.Locator;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using RoslynIndexer.Net48.Adapters;     // MsBuildWorkspaceLoader, GitProbe
using RoslynIndexer.Core.Helpers;       // ArchiveUtils
using RoslynIndexer.Core.Models;
using RoslynIndexer.Core.Pipeline;
using RoslynIndexer.Core.Services;      // RepositoryScanner, FileHasher, CSharpAnalyzer

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynIndexer.Net48
{
    internal class Program
    {
        static void Main(string[] args)
        {
            try
            {
                Console.WriteLine("=== RoslynIndexer.Net48 bootstrap ===");
                Console.WriteLine("[BOOT] Args: " + string.Join(" ", args ?? new string[0]));
                Console.Out.Flush();

                MainAsync(args).GetAwaiter().GetResult();

                Console.WriteLine("=== DONE ===");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FATAL] " + ex);
                Environment.ExitCode = 1;
            }
        }

        private static async Task MainAsync(string[] args)
        {
            var sw = Stopwatch.StartNew();

            // --- 1) CLI parse ---
            var cli = ParseArgs(args);
            Console.WriteLine("[CLI] Parsed " + cli.Count + " arg(s).");

            // --- 2) Optional JSON config (JSON with comments, nested sections) ---
            JObject cfg = null;
            string cfgBaseDir = null;

            var cfgPath = Get(cli, "config");
            if (!string.IsNullOrWhiteSpace(cfgPath))
            {
                if (!File.Exists(cfgPath))
                    throw new ArgumentException("Config file not found: " + cfgPath);

                var loaded = LoadJsonObjectAllowingComments(cfgPath);
                cfg = loaded.Item1;
                cfgBaseDir = loaded.Item2;

                Console.WriteLine("[CFG] Loaded: " + cfgPath);
                Console.WriteLine("[CFG] Root properties: " + string.Join(", ", cfg.Properties().Select(p => p.Name)));
            }
            else
            {
                throw new ArgumentException("Missing --config. The Net48 runner requires msbuild.* paths from config.json.");
            }

            // --- 3) Register MSBuild strictly from config ---
            RegisterMsBuildFromConfig(cfg, cfgBaseDir);

            // --- 4) Resolve inputs (CLI -> cfg.paths -> cfg.* -> ENV) ---
            string solutionPath = FirstNonEmpty(
                FromCli(cli, "solution"),
                FromCfg(cfg, "paths.solution", cfgBaseDir, true),
                FromCfg(cfg, "solution", cfgBaseDir, true),
                Environment.GetEnvironmentVariable("INDEXER_SOLUTION")
            );
            if (string.IsNullOrWhiteSpace(solutionPath))
                throw new ArgumentException("Missing --solution or config['paths.solution'].");

            string tempRoot = FirstNonEmpty(
                FromCli(cli, "temp-root", "temproot"),
                FromCfg(cfg, "paths.tempRoot", cfgBaseDir, true),
                FromCfg(cfg, "tempRoot", cfgBaseDir, true),
                Environment.GetEnvironmentVariable("INDEXER_TEMP_ROOT")
            );
            if (string.IsNullOrWhiteSpace(tempRoot))
                throw new ArgumentException("Missing --temp-root or config['paths.tempRoot'].");

            string sqlPath = FirstNonEmpty(
                FromCli(cli, "sql"),
                FromCfg(cfg, "paths.sql", cfgBaseDir, true),
                FromCfg(cfg, "sql", cfgBaseDir, true)
            );

            string efPath = FirstNonEmpty(
                FromCli(cli, "ef"), FromCli(cli, "migrations"),
                FromCfg(cfg, "paths.ef", cfgBaseDir, true),
                FromCfg(cfg, "ef", cfgBaseDir, true),
                Environment.GetEnvironmentVariable("EF_PATH")
            );

            string inlineSql = FirstNonEmpty(
                FromCli(cli, "inline-sql"),
                FromCfg(cfg, "paths.inlineSql", cfgBaseDir, true),
                FromCfg(cfg, "inlineSql", cfgBaseDir, true)
            );

            string outPath = FirstNonEmpty(
                FromCli(cli, "out"),
                FromCfg(cfg, "paths.out", cfgBaseDir, true)
            );

            // --- 5) (Optional) set MSBuild-related ENV like in Net9 ---
            ApplyMsBuildPathOverrides(cfg, cli, cfgBaseDir);

            // Log inputs (Net9 style)
            Console.WriteLine("[IN] solution   = " + solutionPath);
            Console.WriteLine("[IN] temp-root  = " + tempRoot);
            Console.WriteLine("[IN] out        = " + (outPath ?? "(null)"));
            Console.WriteLine("[IN] sql        = " + (sqlPath ?? "(null)"));
            Console.WriteLine("[IN] ef         = " + (efPath ?? "(null)"));
            Console.WriteLine("[IN] inline-sql = " + (inlineSql ?? "(null)"));
            Console.Out.Flush();

            Directory.CreateDirectory(tempRoot);

            // --- 6) Core pipeline: scan + hash + Roslyn quick stats ---
            var scanner = new RepositoryScanner();
            var hasher = new FileHasher();
            var cs = new CSharpAnalyzer();
            var pipeline = new IndexingPipeline(scanner, hasher, cs, sqlExtractor: null);

            var paths = new RepoPaths(
                repoRoot: tempRoot,
                solutionPath: solutionPath,
                sqlPath: sqlPath,
                efMigrationsPath: null,   // parity with Net9 runner – migrations handled separately if needed
                inlineSqlPath: inlineSql
            );

            var loader = new MsBuildWorkspaceLoader();

            Console.WriteLine("[Core] Starting pipeline...");
            var tuple = await pipeline.RunAsync(paths, loader, CancellationToken.None);
            var files = tuple.files;
            var csharp = tuple.csharp;

            Console.WriteLine("[Core] Files   : " + files.Count);
            Console.WriteLine("[Core] Projects: " + csharp.ProjectCount);
            Console.WriteLine("[Core] Docs    : " + csharp.DocumentCount);
            Console.WriteLine("[Core] Methods : " + csharp.MethodCount);

            // --- 7) Legacy SQL/EF GRAPH (optional; produces sql_bodies.jsonl expected by downstream pipeline) ---
            if (!string.IsNullOrWhiteSpace(sqlPath))
            {
                // Guard: provided but folder missing → fail fast (avoids silent skip)
                if (!Directory.Exists(sqlPath))
                    throw new DirectoryNotFoundException("[SQL] Path not found: " + sqlPath);

                var efExists = !string.IsNullOrWhiteSpace(efPath) && Directory.Exists(efPath);
                Console.WriteLine(efExists
                    ? "[SQL/EF] Using EF root: " + efPath
                    : "[SQL/EF] EF root not provided or missing; graph will include SQL only.");

                // Fully-qualified to avoid adding a using outside this method
                RoslynIndexer.Core.Sql.LegacySqlIndexer.Start(
                    outputDir: tempRoot,
                    sqlProjectRoot: sqlPath,
                    efRoot: efExists ? efPath : null
                );

                // Verify artifact expected by Python/Vector pipeline
                var sqlBodies = Directory
                    .EnumerateFiles(tempRoot, "sql_bodies.jsonl", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (sqlBodies == null)
                    throw new InvalidOperationException(
                        "[SQL] Expected 'sql_bodies.jsonl' not produced under " + tempRoot +
                        ". Verify 'paths.sql' points to the SQL project root.");

                Console.WriteLine("[SQL] Produced: " + sqlBodies);
            }
            else
            {
                Console.WriteLine("[SQL] Skipped (no --sql).");
            }

            // --- 8) Git metadata (adapter) ---
            var git = GitProbe.TryProbe(solutionPath);
            Console.WriteLine("[GIT] branch=" + (git.Branch ?? "(unknown)") + " head=" + (git.HeadSha ?? "(unknown)"));

            // --- 9) Extract code chunks + deps and write artifacts ---
            var solution = await loader.LoadSolutionAsync(solutionPath, CancellationToken.None);
            var extractor = new CodeChunkExtractor();

            var result = await extractor.ExtractAsync(
                solution,
                repoRoot: git.RepoRoot ?? Path.GetDirectoryName(solutionPath),
                branchName: git.Branch ?? new DirectoryInfo(tempRoot).Name,
                headSha: git.HeadSha ?? "(unknown)",
                CancellationToken.None);

            var chunks = result.chunks;
            var deps = result.deps;

            var codeOutDir = Path.Combine(tempRoot, "regular_code_bundle");
            var meta = new RepoMeta
            {
                Branch = git.Branch ?? "",
                HeadSha = git.HeadSha ?? "",
                RepositoryRoot = git.RepoRoot ?? "",
                GeneratedAtUtc = DateTime.UtcNow
            };

            ArtifactWriter.WriteCodeArtifacts(
                branchRoot: tempRoot,
                codeOutDir: codeOutDir,
                chunks: chunks,
                deps: deps,
                meta: meta
            );

            // --- 10) ZIP + cleanup (branch-named zip; copy to 'out' if provided, timestamp on collision) ---
            try
            {
                Console.WriteLine("[ZIP] Target out: " + (outPath ?? "(null)"));
                var zipPath = ArchiveUtils.CreateZipAndDeleteForBranch(tempRoot, git.Branch, outPath);
                Console.WriteLine("[ZIP] Created: " + zipPath);

                if (!string.IsNullOrWhiteSpace(outPath))
                {
                    var outFull = Path.GetFullPath(outPath);
                    var zipFull = Path.GetFullPath(zipPath);
                    if (!zipFull.StartsWith(outFull, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine("[ZIP] WARNING: copy to 'out' failed or was skipped; using local archive.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERR] Archiving: " + ex.Message);
            }

            sw.Stop();
            Console.WriteLine("[TIME] Total: " + sw.Elapsed);
        }



        // ---------------- MSBuild registration (from config) ----------------

        /// <summary>
        /// Registers MSBuild strictly from config.msbuild.* values.
        /// Uses MSBuildExtensionsPath32\Current\Bin. No heuristics, no auto-detection.
        /// Also wires AssemblyResolve to load Microsoft.Build* and Microsoft.NET.StringTools
        /// from that directory, while not forcing Microsoft.IO.Redist.
        /// </summary>
        private static void RegisterMsBuildFromConfig(JObject cfg, string baseDir)
        {
            if (cfg == null)
                throw new ArgumentException("Config is required to register MSBuild for the Net48 runner.");

            var msbExt32 = FromCfg(cfg, "msbuild.MSBuildExtensionsPath32", baseDir, true);
            var vsTools = FromCfg(cfg, "msbuild.VSToolsPath", baseDir, true);
            var vsVer = FirstNonEmpty(FromCfg(cfg, "msbuild.VisualStudioVersion", baseDir, false), "17.0");

            if (string.IsNullOrWhiteSpace(msbExt32))
                throw new ArgumentException("Config 'msbuild.MSBuildExtensionsPath32' is required.");
            if (!Directory.Exists(msbExt32))
                throw new DirectoryNotFoundException("MSBuildExtensionsPath32 not found: " + msbExt32);

            // The Bin folder that contains MSBuild.exe and its assemblies.
            var bin = Path.Combine(msbExt32, "Current", "Bin");
            if (!Directory.Exists(bin))
                throw new DirectoryNotFoundException("MSBuild 'Current\\Bin' not found under: " + msbExt32);

            // Set environment to match the configured instance.
            Environment.SetEnvironmentVariable("VisualStudioVersion", vsVer);
            Environment.SetEnvironmentVariable("MSBuildExtensionsPath32", msbExt32);
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSBuildExtensionsPath")))
                Environment.SetEnvironmentVariable("MSBuildExtensionsPath", msbExt32);
            if (!string.IsNullOrWhiteSpace(vsTools))
                Environment.SetEnvironmentVariable("VSToolsPath", vsTools);

            // Register this exact MSBuild.
            MSBuildLocator.RegisterMSBuildPath(bin);
            Console.WriteLine("[MSBuild] Using config instance @ " + bin);

            // Optional: light diagnostics for critical assemblies.
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
            {
                var n = e.LoadedAssembly.GetName().Name;
                if (n.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("Microsoft.NET.StringTools", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("Microsoft.IO.Redist", StringComparison.OrdinalIgnoreCase))
                {
                    string loc;
                    try { loc = e.LoadedAssembly.Location; } catch { loc = "(dynamic)"; }
                    Console.WriteLine("[ASM] " + e.LoadedAssembly.FullName + " <= " + loc);
                }
            };

            // Resolve Microsoft.Build* and Microsoft.NET.StringTools from the chosen Bin.
            // Do NOT force Microsoft.IO.Redist; let the runtime pick the compatible one.
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var an = new AssemblyName(args.Name);

                if (string.Equals(an.Name, "Microsoft.NET.StringTools", StringComparison.OrdinalIgnoreCase))
                {
                    var p = Path.Combine(bin, "Microsoft.NET.StringTools.dll");
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }

                if (string.Equals(an.Name, "Microsoft.Build", StringComparison.OrdinalIgnoreCase) ||
                    an.Name.StartsWith("Microsoft.Build.", StringComparison.OrdinalIgnoreCase))
                {
                    var p = Path.Combine(bin, an.Name + ".dll");
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }

                return null;
            };
        }

        // ---------------- Helpers ----------------

        // Accepts: "--name value", "-name value", "name=value"
        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (args == null) return map;

            for (int i = 0; i < args.Length; i++)
            {
                var token = args[i] ?? string.Empty;

                if (token.StartsWith("--") || token.StartsWith("-"))
                {
                    var key = token.TrimStart('-');
                    string val = (i + 1 < args.Length && !(args[i + 1] ?? string.Empty).StartsWith("-"))
                                 ? args[++i]
                                 : "true";
                    map[key] = (val ?? string.Empty).Trim('"');
                }
                else
                {
                    var eq = token.IndexOf('=');
                    if (eq > 0)
                    {
                        var key = token.Substring(0, eq).TrimStart('-');
                        var val = token.Substring(eq + 1);
                        map[key] = (val ?? string.Empty).Trim('"');
                    }
                }
            }
            return map;
        }

        private static Tuple<JObject, string> LoadJsonObjectAllowingComments(string path)
        {
            var text = File.ReadAllText(path);
            using (var sr = new StringReader(text))
            using (var reader = new JsonTextReader(sr))
            {
                var loadSettings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore };
                var token = JToken.ReadFrom(reader, loadSettings);
                var jobj = token as JObject ?? new JObject();
                var baseDir = Path.GetDirectoryName(path) ?? "";
                return Tuple.Create(jobj, baseDir);
            }
        }

        private static string FromCli(Dictionary<string, string> cli, params string[] keys)
        {
            foreach (var k in keys)
            {
                string v;
                if (cli.TryGetValue(k, out v) && !string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return null;
        }

        private static string FromCfg(JObject cfg, string dottedPath, string baseDir, bool makeAbsolute)
        {
            if (cfg == null) return null;
            var tok = cfg.SelectToken(dottedPath, false);
            if (tok == null) return null;

            var val = tok.Type == JTokenType.String ? (string)tok : tok.ToString();
            if (string.IsNullOrWhiteSpace(val)) return null;

            if (makeAbsolute && !Path.IsPathRooted(val) && !string.IsNullOrEmpty(baseDir))
                val = Path.GetFullPath(Path.Combine(baseDir, val));

            return val;
        }

        private static string FirstNonEmpty(params string[] vals)
        {
            foreach (var v in vals)
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            return null;
        }

        /// <summary>
        /// Applies MSBuild-related env vars from config/CLI to mirror the Net9 runner.
        /// </summary>
        private static void ApplyMsBuildPathOverrides(JObject cfg, Dictionary<string, string> cli, string baseDir)
        {
            var vsVer = FirstNonEmpty(
                FromCli(cli, "vsver", "visualstudioversion"),
                FromCfg(cfg, "msbuild.VisualStudioVersion", baseDir, false),
                Environment.GetEnvironmentVariable("VisualStudioVersion"),
                "17.0"
            );

            var vsTools = FirstNonEmpty(
                FromCli(cli, "vstools", "msbuild.vstools"),
                FromCfg(cfg, "msbuild.VSToolsPath", baseDir, true),
                Environment.GetEnvironmentVariable("VSToolsPath")
            );

            var msbExt32 = FirstNonEmpty(
                FromCli(cli, "msbuildextensionspath32"),
                FromCfg(cfg, "msbuild.MSBuildExtensionsPath32", baseDir, true),
                Environment.GetEnvironmentVariable("MSBuildExtensionsPath32"),
                !string.IsNullOrWhiteSpace(vsTools) ? Path.GetFullPath(Path.Combine(vsTools, "..", "..")) : null
            );

            if (!string.IsNullOrWhiteSpace(vsVer))
                Environment.SetEnvironmentVariable("VisualStudioVersion", vsVer);

            if (!string.IsNullOrWhiteSpace(vsTools))
                Environment.SetEnvironmentVariable("VSToolsPath", vsTools);

            if (!string.IsNullOrWhiteSpace(msbExt32))
            {
                Environment.SetEnvironmentVariable("MSBuildExtensionsPath32", msbExt32);
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSBuildExtensionsPath")))
                    Environment.SetEnvironmentVariable("MSBuildExtensionsPath", msbExt32);
            }

            var transformXml = FirstNonEmpty(
                FromCli(cli, "transformxml", "msbuild.transformxml"),
                FromCfg(cfg, "externalTasks.TransformXml", baseDir, true),
                !string.IsNullOrWhiteSpace(vsTools) ? Path.Combine(vsTools, "Web", "Microsoft.Web.Publishing.Tasks.dll") : null,
                Environment.GetEnvironmentVariable("TRANSFORMXML_PATH")
            );

            if (!string.IsNullOrWhiteSpace(transformXml) && File.Exists(transformXml))
                Environment.SetEnvironmentVariable("TRANSFORMXML_PATH", transformXml);
        }

        private static string Get(Dictionary<string, string> map, string key)
        {
            string val;
            return map.TryGetValue(key, out val) ? val : null;
        }
    }
}
