using Microsoft.Build.Locator;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using RoslynIndexer.Net48.Adapters;     // MsBuildWorkspaceLoader, GitProbe
using RoslynIndexer.Core.Helpers;       // ArchiveUtils
using RoslynIndexer.Core.Models;
using RoslynIndexer.Core.Pipeline;
using RoslynIndexer.Core.Services;      // RepositoryScanner, FileHasher, CSharpAnalyzer
using RoslynIndexer.Core.Sql;           // SqlEfGraphRunner, LegacySqlIndexer
using RoslynIndexer.Core.Logging;       // ConsoleLog

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
                ConsoleLog.Info("=== RoslynIndexer.Net48 bootstrap ===");
                ConsoleLog.Info("[BOOT] Args: " + string.Join(" ", args ?? new string[0]));
                Console.Out.Flush();

                MainAsync(args).GetAwaiter().GetResult();

                ConsoleLog.Info("=== DONE ===");
            }
            catch (Exception ex)
            {
                ConsoleLog.Error("[FATAL] " + ex);
                Environment.ExitCode = 1;
            }
        }

        private static async Task MainAsync(string[] args)
        {
            var sw = Stopwatch.StartNew();

            // ===============================
            // 1) MSBuild registration (FIRST)
            // ===============================
            if (!MSBuildLocator.IsRegistered)
            {
                var vs = MSBuildLocator
                    .QueryVisualStudioInstances()
                    .OrderByDescending(v => v.Version)
                    .FirstOrDefault();

                if (vs != null)
                {
                    ConsoleLog.Info($"[MSBuild] Using {vs.Name} {vs.Version} @ {vs.VisualStudioRootPath}");
                    MSBuildLocator.RegisterInstance(vs);
                }
                else
                {
                    ConsoleLog.Info("[MSBuild] No VS instance found – using RegisterDefaults()");
                    MSBuildLocator.RegisterDefaults();
                }
            }

            // ===============================
            // 2) CLI + CONFIG (JSONC) + ENV
            // ===============================
            var cli = ParseArgs(args);
            ConsoleLog.Info("[CLI] Parsed " + cli.Count + " arg(s).");

            // Quiet / errors-only
            var quiet =
                (cli.TryGetValue("log", out var lv) &&
                 string.Equals(lv, "error", StringComparison.OrdinalIgnoreCase))
                || cli.ContainsKey("quiet");

            if (quiet)
                Console.SetOut(TextWriter.Null);

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

                ConsoleLog.Info("[CFG] Loaded: " + cfgPath);
                ConsoleLog.Info("[CFG] Root properties: " + string.Join(", ", cfg.Properties().Select(p => p.Name)));

                // Inject dbGraph config into LegacySqlIndexer
                try
                {
                    LegacySqlIndexer.GlobalDbGraphConfig =
                        cfg != null ? DbGraphConfig.FromJson(cfg) : DbGraphConfig.Empty;
                }
                catch (Exception ex)
                {
                    ConsoleLog.Warn("[dbGraph] Failed to parse dbGraph from main config.json: " + ex.Message);
                    LegacySqlIndexer.GlobalDbGraphConfig = DbGraphConfig.Empty;
                }
            }
            else
            {
                ConsoleLog.Info("[CFG] No config provided. Use --config \"D:\\\\config.json\"");
                LegacySqlIndexer.GlobalDbGraphConfig = DbGraphConfig.Empty;
            }

            // ===============================
            // 3) Resolve inputs (CLI -> cfg.paths -> cfg.* -> ENV)
            //    Relative paths -> relative to config folder
            // ===============================
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

            // Optional SQL root (new schema: paths.sqlRoot / sqlRoot, plus legacy: paths.sql / sql)
            string sqlPath = FirstNonEmpty(
                FromCli(cli, "sql"),
                FromCfg(cfg, "paths.sqlRoot", cfgBaseDir, true),
                FromCfg(cfg, "sqlRoot", cfgBaseDir, true),
                FromCfg(cfg, "paths.sql", cfgBaseDir, true),
                FromCfg(cfg, "sql", cfgBaseDir, true)
            );

            // EF migrations root (new: paths.migrationsRoot / migrationsRoot; old: paths.migrations / migrations)
            string efMigrationsPath = FirstNonEmpty(
                FromCli(cli, "migrations"),
                FromCfg(cfg, "paths.migrationsRoot", cfgBaseDir, true),
                FromCfg(cfg, "migrationsRoot", cfgBaseDir, true),
                FromCfg(cfg, "paths.migrations", cfgBaseDir, true),
                FromCfg(cfg, "migrations", cfgBaseDir, true),
                Environment.GetEnvironmentVariable("MIGRATIONS_PATH")
            );

            // EF root (new: paths.modelRoot / modelRoot; legacy: paths.ef / ef; env; fallback: migrations path)
            string efPath = FirstNonEmpty(
                FromCli(cli, "model"),
                FromCfg(cfg, "paths.modelRoot", cfgBaseDir, true),
                FromCfg(cfg, "modelRoot", cfgBaseDir, true),
                FromCfg(cfg, "paths.ef", cfgBaseDir, true),
                FromCfg(cfg, "ef", cfgBaseDir, true),
                Environment.GetEnvironmentVariable("EF_PATH"),
                efMigrationsPath
            );

            // Inline SQL root (new: paths.inlineSqlRoot / inlineSql; legacy: paths.inlineSql)
            string inlineSql = FirstNonEmpty(
                FromCli(cli, "inline-sql"),
                FromCfg(cfg, "paths.inlineSqlRoot", cfgBaseDir, true),
                FromCfg(cfg, "inlineSql", cfgBaseDir, true),
                FromCfg(cfg, "paths.inlineSql", cfgBaseDir, true)
            );

            // Output directory (new: paths.outRoot; plus legacy: CLI out / paths.out / out)
            string outDir = FirstNonEmpty(
                FromCli(cli, "outRoot", "out"),
                FromCfg(cfg, "paths.outRoot", cfgBaseDir, true),
                FromCfg(cfg, "paths.out", cfgBaseDir, true),
                FromCfg(cfg, "out", cfgBaseDir, true)
            );

            // Inline-SQL: extra "hot" methods configured via config.inlineSql.extraHotMethods
            var inlineSqlHotMethods = Array.Empty<string>();
            if (cfg != null)
            {
                var inlineSqlSection = cfg["inlineSql"] as JObject;
                if (inlineSqlSection != null)
                {
                    var extraHot = inlineSqlSection["extraHotMethods"] as JArray;
                    if (extraHot != null && extraHot.Count > 0)
                    {
                        inlineSqlHotMethods = extraHot
                            .OfType<JValue>()
                            .Select(v => v.Value as string)
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s.Trim())
                            .ToArray();
                    }
                }
            }

            LegacySqlIndexer.GlobalInlineSqlHotMethods = inlineSqlHotMethods;

            // Global EF migrations + inline SQL roots for LegacySqlIndexer
            LegacySqlIndexer.GlobalEfMigrationRoots = !string.IsNullOrWhiteSpace(efMigrationsPath)
                ? new[] { efMigrationsPath }
                : Array.Empty<string>();

            LegacySqlIndexer.GlobalInlineSqlRoots = !string.IsNullOrWhiteSpace(inlineSql)
                ? new[] { inlineSql }
                : Array.Empty<string>();

            // ===============================
            // 4) MSBuild environment overrides (TransformXml etc.)
            // ===============================
            ApplyMsBuildPathOverrides(cfg, cli, cfgBaseDir);

            // Log inputs (same style as Net9)
            ConsoleLog.Info("[IN] solution   = " + solutionPath);
            ConsoleLog.Info("[IN] temp-root  = " + tempRoot);
            ConsoleLog.Info("[IN] out-root        = " + (outDir ?? "(null)"));
            ConsoleLog.Info("[IN] sql-root        = " + (sqlPath ?? "(null)"));
            ConsoleLog.Info("[IN] model-root (eg EF POCOs) = " + (efPath ?? "(null)"));
            ConsoleLog.Info("[IN] migrations-root = " + (efMigrationsPath ?? "(null)"));
            ConsoleLog.Info("[IN] inlineSql-root = " + (inlineSql ?? "(null)"));
            Console.Out.Flush();

            Directory.CreateDirectory(tempRoot);

            // ===============================
            // 5) Core pipeline: scan + hash + Roslyn quick stats
            //     (with ScriptDomSqlModelExtractor_waiting, like Net9)
            // ===============================
            var scanner = new RepositoryScanner();
            var hasher = new FileHasher();
            var cs = new CSharpAnalyzer();
            var sqlExtractor = new ScriptDomSqlModelExtractor_waiting();
            var pipeline = new IndexingPipeline(scanner, hasher, cs, sqlExtractor);

            var paths = new RepoPaths(
                repoRoot: tempRoot,
                solutionPath: solutionPath,
                sqlPath: sqlPath,
                efMigrationsPath: null,   // EF graph is handled separately by LegacySqlIndexer
                inlineSqlPath: inlineSql
            );

            var loader = new MsBuildWorkspaceLoader();

            ConsoleLog.Info("[Core] Starting pipeline...");
            var tuple = await pipeline.RunAsync(paths, loader, CancellationToken.None);
            var files = tuple.files;
            var csharp = tuple.csharp;

            ConsoleLog.Info("[Core] Files   : " + files.Count);
            ConsoleLog.Info("[Core] Projects: " + csharp.ProjectCount);
            ConsoleLog.Info("[Core] Docs    : " + csharp.DocumentCount);
            ConsoleLog.Info("[Core] Methods : " + csharp.MethodCount);

            // Migrations-only fallback: if there is no explicit EF root but migrations are configured,
            // treat efMigrationsPath as EF root.
            if (string.IsNullOrWhiteSpace(efPath) && !string.IsNullOrWhiteSpace(efMigrationsPath))
            {
                efPath = efMigrationsPath;
            }

            // Inline-SQL fallback: if EF root is still empty but inlineSql is configured,
            // use inlineSql as EF root so LegacySqlIndexer can scan C# for raw SQL usage.
            if (string.IsNullOrWhiteSpace(efPath) && !string.IsNullOrWhiteSpace(inlineSql))
            {
                efPath = inlineSql;
            }

            // ===============================
            // 6) Legacy SQL/EF GRAPH (optional; produces sql_bodies.jsonl)
            // ===============================
            SqlEfGraphRunner.Run(
                tempRoot: tempRoot,
                sqlPath: sqlPath ?? string.Empty,
                efPath: efPath ?? string.Empty,
                solutionPath: solutionPath);

            // ===============================
            // 7) Git metadata (adapter)
            // ===============================
            var git = GitProbe.TryProbe(solutionPath);
            ConsoleLog.Info("[GIT] branch=" + (git.Branch ?? "(unknown)") + " head=" + (git.HeadSha ?? "(unknown)"));

            // ===============================
            // 8) Extract code chunks + deps and write artifacts
            // ===============================
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

            // ===============================
            // 9) ZIP + cleanup (branch-named zip; copy to 'out' if provided)
            // ===============================
            try
            {
                ConsoleLog.Info("[ZIP] Target out: " + (outDir ?? "(null)"));
                var zipPath = ArchiveUtils.CreateZipAndDeleteForBranch(tempRoot, git.Branch, outDir);
                ConsoleLog.Info("[ZIP] Created: " + zipPath);

                if (!string.IsNullOrWhiteSpace(outDir))
                {
                    var outFull = Path.GetFullPath(outDir);
                    var zipFull = Path.GetFullPath(zipPath);
                    if (!zipFull.StartsWith(outFull, StringComparison.OrdinalIgnoreCase))
                        ConsoleLog.Warn("[ZIP] WARNING: copy to 'out' failed or was skipped; using local archive.");
                }
            }
            catch (Exception ex)
            {
                ConsoleLog.Error("[ERR] Archiving: " + ex.Message);
            }

            sw.Stop();
            ConsoleLog.Info("[TIME] Total: " + sw.Elapsed);
        }

        // ---------------- Helpers ----------------

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
                if (cli.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return null;
        }

        private static string FromCfg(JObject cfg, string dottedPath, string baseDir, bool makeAbsolute)
        {
            if (cfg == null) return null;

            var tok = cfg.SelectToken(dottedPath, errorWhenNoMatch: false);
            if (tok == null) return null;

            // 1) Jeżeli oczekujemy ścieżki (makeAbsolute == true), akceptujemy TYLKO stringi.
            if (makeAbsolute && tok.Type != JTokenType.String)
            {
                // Np. inlineSql = { extraHotMethods: [...] } – to NIE jest path.
                return null;
            }

            string val;

            if (tok.Type == JTokenType.String)
            {
                val = (string)tok;
            }
            else
            {
                // makeAbsolute == false, można bezpiecznie użyć ToString()
                val = tok.ToString();
            }

            if (string.IsNullOrWhiteSpace(val))
                return null;

            if (!makeAbsolute)
                return val;

            // 2) Dla ścieżek robimy ostrożne Path.Combine/Path.GetFullPath.
            try
            {
                if (!Path.IsPathRooted(val) && !string.IsNullOrEmpty(baseDir))
                    val = Path.GetFullPath(Path.Combine(baseDir, val));
            }
            catch (ArgumentException)
            {
                // Jeżeli w configu naprawdę jest śmieć jako "ścieżka" – po prostu pomijamy.
                return null;
            }

            return val;
        }


        private static string FirstNonEmpty(params string[] vals)
        {
            foreach (var v in vals)
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            return null;
        }

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
            return map.TryGetValue(key, out var val) ? val : null;
        }

        // (RegisterMsBuildFromConfig left here unused on purpose; Net48 now uses the same
        //  MSBuildLocator auto-detection as Net9. Can be removed later if not needed.)
    }
}
