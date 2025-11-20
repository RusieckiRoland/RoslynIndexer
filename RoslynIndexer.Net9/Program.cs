// RoslynIndexer.Net9/Program.cs
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using RoslynIndexer.Core.Abstractions;
using RoslynIndexer.Core.Helpers;   // GitProbe, ArchiveUtils
using RoslynIndexer.Core.Logging;   // ConsoleLog
using RoslynIndexer.Core.Models;
using RoslynIndexer.Core.Pipeline;
using RoslynIndexer.Core.Services;  // RepositoryScanner, FileHasher, CSharpAnalyzer, CodeChunkExtractor, ArtifactWriter
using RoslynIndexer.Core.Sql;       // LegacySqlIndexer, SqlEfGraphRunner
using RoslynIndexer.Net9.Adapters;  // MsBuildWorkspaceLoader

internal class Program
{
    private static async Task Main(string[] args)
    {
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
        // 2) Host + DI
        // ===============================
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services
            .AddSingleton<IWorkspaceLoader, MsBuildWorkspaceLoader>()
            .AddSingleton<IRepositoryScanner, RepositoryScanner>()
            .AddSingleton<IFileHasher, FileHasher>()
            .AddSingleton<ICSharpAnalyzer, CSharpAnalyzer>()
            .AddSingleton<IndexingPipeline>();

        var app = builder.Build();

        // ===============================
        // 3) CLI + CONFIG (JSONC) + ENV
        // ===============================
        var cli = ParseArgs(args);

        // Quiet/Errors-only
        var quiet = cli.TryGetValue("log", out var lv) && string.Equals(lv, "error", StringComparison.OrdinalIgnoreCase)
                    || cli.ContainsKey("quiet");
        if (quiet) Console.SetOut(TextWriter.Null);

        JObject? cfg = null;
        string? cfgBaseDir = null;

        var cfgPath = cli.GetValueOrDefault("config");
        if (!string.IsNullOrWhiteSpace(cfgPath))
        {
            if (!File.Exists(cfgPath))
                throw new ArgumentException($"Config file not found: {cfgPath}");

            (cfg, cfgBaseDir) = LoadJsonObjectAllowingComments(cfgPath);
            ConsoleLog.Info($"[CFG] Loaded: {cfgPath}");

            // ⬇️ TU: wstrzyknięcie dbGraph z GŁÓWNEGO config.json do LegacySqlIndexer
            try
            {
                LegacySqlIndexer.GlobalDbGraphConfig =
                    cfg is not null ? DbGraphConfig.FromJson(cfg) : DbGraphConfig.Empty;
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
            // brak configa → brak dbGraph
            LegacySqlIndexer.GlobalDbGraphConfig = DbGraphConfig.Empty;
        }


        // ===============================
        // 4) Resolve inputs (CLI -> cfg.paths -> cfg.* -> ENV)
        //     Ścieżki względne → względem folderu configa
        // ===============================
        string solutionPath = FirstNonEmpty(
            FromCli(cli, "solution"),
            FromCfg(cfg, "paths.solution", cfgBaseDir, makeAbsolute: true),
            FromCfg(cfg, "solution", cfgBaseDir, makeAbsolute: true),
            Environment.GetEnvironmentVariable("INDEXER_SOLUTION")
        );
        if (string.IsNullOrWhiteSpace(solutionPath))
            throw new ArgumentException("Missing --solution or config['paths.solution'].");

        string tempRoot = FirstNonEmpty(
            FromCli(cli, "temp-root", "temproot"),
            FromCfg(cfg, "paths.tempRoot", cfgBaseDir, makeAbsolute: true),
            FromCfg(cfg, "tempRoot", cfgBaseDir, makeAbsolute: true),
            Environment.GetEnvironmentVariable("INDEXER_TEMP_ROOT")
        );
        if (string.IsNullOrWhiteSpace(tempRoot))
            throw new ArgumentException("Missing --temp-root or config['paths.tempRoot'].");

        // ----- Optional -----
        string? sqlPath = FirstNonEmpty(
            FromCli(cli, "sql"),
            FromCfg(cfg, "paths.sql", cfgBaseDir, makeAbsolute: true),
            FromCfg(cfg, "sql", cfgBaseDir, makeAbsolute: true)
        );

        // Explicit migrations root (can be used even without classic EF DbContext)
        string? migrationsPath = FirstNonEmpty(
            FromCli(cli, "migrations"),
            FromCfg(cfg, "paths.migrations", cfgBaseDir, makeAbsolute: true),
            FromCfg(cfg, "migrations", cfgBaseDir, makeAbsolute: true),
            Environment.GetEnvironmentVariable("MIGRATIONS_PATH")
        );

        // EF root used by LegacySqlIndexer (DbContext + migrations or migrations-only)
        string? efPath = FirstNonEmpty(
            FromCli(cli, "ef"),
            FromCfg(cfg, "paths.ef", cfgBaseDir, makeAbsolute: true),
            FromCfg(cfg, "ef", cfgBaseDir, makeAbsolute: true),
            Environment.GetEnvironmentVariable("EF_PATH"),
            migrationsPath        // fallback: treat migrations path as EF root
        );

        string? inlineSql = FirstNonEmpty(
            FromCli(cli, "inline-sql"),
            FromCfg(cfg, "paths.inlineSql", cfgBaseDir, makeAbsolute: true),
            FromCfg(cfg, "inlineSql", cfgBaseDir, makeAbsolute: true)
        );

        string? outDir = FirstNonEmpty(
            FromCli(cli, "out"),
            FromCfg(cfg, "paths.out", cfgBaseDir, makeAbsolute: true)
        );

        LegacySqlIndexer.GlobalEfMigrationRoots =   !string.IsNullOrWhiteSpace(migrationsPath)
        ? new[] { migrationsPath }
        : Array.Empty<string>();

        // ===============================
        // 5) Ustaw MSBuild ścieżki (TransformXml itd.)
        // ===============================
        ApplyMsBuildPathOverrides(cfg, cli, cfgBaseDir);

        // Log inputs
        ConsoleLog.Info("[IN] solution   = " + solutionPath);
        ConsoleLog.Info("[IN] temp-root  = " + tempRoot);
        ConsoleLog.Info("[IN] out        = " + (outDir ?? "(null)"));
        ConsoleLog.Info("[IN] sql        = " + (sqlPath ?? "(null)"));
        ConsoleLog.Info("[IN] ef         = " + (efPath ?? "(null)"));
        ConsoleLog.Info("[IN] migrations = " + (migrationsPath ?? "(null)"));
        ConsoleLog.Info("[IN] inline-sql = " + (inlineSql ?? "(null)"));

        // Ensure temp dir
        Directory.CreateDirectory(tempRoot);

        // ===============================
        // 6) Pipeline: scan/hash/load/analyze
        // ===============================
        var pipeline = app.Services.GetRequiredService<IndexingPipeline>();
        var loader = app.Services.GetRequiredService<IWorkspaceLoader>();

        var paths = new RepoPaths(
            repoRoot: tempRoot,
            solutionPath: solutionPath,
            sqlPath: sqlPath,
            efMigrationsPath: null,              // EF graph robi LegacySqlIndexer osobno
            inlineSqlPath: inlineSql
        );

        var (files, csharp, _) = await pipeline.RunAsync(paths, loader, CancellationToken.None);
        ConsoleLog.Info("[Core] Files   : " + files.Count);
        ConsoleLog.Info("[Core] Projects: " + csharp.ProjectCount);
        ConsoleLog.Info("[Core] Docs    : " + csharp.DocumentCount);
        ConsoleLog.Info("[Core] Methods : " + csharp.MethodCount);

        // migrations-only fallback: if there is no explicit EF root but migrations are configured,
        // treat migrationsPath as EF root so that SQL/EF runner has a code root for C# scanning.
        if (string.IsNullOrWhiteSpace(efPath) && !string.IsNullOrWhiteSpace(migrationsPath))
        {
            efPath = migrationsPath;
        }


        // ===============================
        // 7) Legacy SQL/EF GRAPH (optional)
        // ===============================
        SqlEfGraphRunner.Run(
            tempRoot: tempRoot,
            sqlPath: sqlPath ?? string.Empty,
            efPath: efPath ?? string.Empty);

        // ===============================
        // 8) Git + chunks/deps → artifacts
        // ===============================
        var git = GitProbe.TryProbe(solutionPath);
        ConsoleLog.Info("[GIT] branch=" + (git.Branch ?? "(unknown)") + " head=" + (git.HeadSha ?? "(unknown)"));

        var solution = await loader.LoadSolutionAsync(solutionPath, CancellationToken.None);
        var extractor = new CodeChunkExtractor();

        var (chunks, deps) = await extractor.ExtractAsync(
            solution,
            repoRoot: git.RepoRoot ?? Path.GetDirectoryName(solutionPath)!,
            branchName: git.Branch ?? new DirectoryInfo(tempRoot).Name,
            headSha: git.HeadSha ?? "(unknown)",
            CancellationToken.None
        );

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
        // 9) ZIP + cleanup
        // ===============================
        try
        {
            ConsoleLog.Info("[ZIP] Target out: " + (outDir ?? "(null)"));

            // nazwij zip jak branch, spakuj bez folderu top-level
            // i skopiuj do 'out' (doklej timestamp przy kolizji)
            var zipPath = ArchiveUtils.CreateZipAndDeleteForBranch(tempRoot, git.Branch, outDir);

            ConsoleLog.Info("[ZIP] Created: " + zipPath);

            // jeżeli kopiowanie do 'out' nie wyszło, daj ostrzeżenie
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

        // ===============================
        // Helpers (statyczne – bez przechwytywania lokalnych zmiennych)
        // ===============================
        static Dictionary<string, string> ParseArgs(string[]? args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (args == null) return map;

            for (int i = 0; i < args.Length; i++)
            {
                var token = args[i] ?? string.Empty;

                if (token.StartsWith("--") || token.StartsWith("-"))
                {
                    var key = token.TrimStart('-');
                    string val = i + 1 < args.Length && !(args[i + 1] ?? string.Empty).StartsWith("-")
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

        static (JObject obj, string baseDir) LoadJsonObjectAllowingComments(string path)
        {
            var text = File.ReadAllText(path);
            using var sr = new StringReader(text);
            using var reader = new JsonTextReader(sr);
            var loadSettings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore };
            var token = JToken.ReadFrom(reader, loadSettings);
            var jobj = token as JObject ?? new JObject();
            var baseDir = Path.GetDirectoryName(path)!;
            ConsoleLog.Info("[CFG] Root properties: " + string.Join(", ", jobj.Properties().Select(p => p.Name)));
            return (jobj, baseDir);
        }

        static string? FromCli(Dictionary<string, string> cli, params string[] keys)
        {
            foreach (var k in keys)
                if (cli.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v;
            return null;
        }

        static string? FromCfg(JObject? cfg, string dottedPath, string? baseDir, bool makeAbsolute)
        {
            if (cfg is null) return null;
            var tok = cfg.SelectToken(dottedPath, errorWhenNoMatch: false);
            if (tok is null) return null;

            var val = tok.Type == JTokenType.String ? (string?)tok : tok.ToString();
            if (string.IsNullOrWhiteSpace(val)) return null;

            if (makeAbsolute && !Path.IsPathRooted(val) && !string.IsNullOrEmpty(baseDir))
                val = Path.GetFullPath(Path.Combine(baseDir!, val));

            return val;
        }

        static string FirstNonEmpty(params string?[] vals)
        {
            foreach (var v in vals)
                if (!string.IsNullOrWhiteSpace(v))
                    return v!;
            return "";
        }

        static void ApplyMsBuildPathOverrides(JObject? cfg, Dictionary<string, string> cli, string? baseDir)
        {
            // VisualStudioVersion (np. 17.0)
            var vsVer = FirstNonEmpty(
                FromCli(cli, "vsver", "visualstudioversion"),
                FromCfg(cfg, "msbuild.VisualStudioVersion", baseDir, false),
                Environment.GetEnvironmentVariable("VisualStudioVersion"),
                "17.0"
            );

            // VSToolsPath: ...\MSBuild\Microsoft\VisualStudio\v17.0
            var vsTools = FirstNonEmpty(
                FromCli(cli, "vstools", "msbuild.vstools"),
                FromCfg(cfg, "msbuild.VSToolsPath", baseDir, true),
                Environment.GetEnvironmentVariable("VSToolsPath")
            );

            // MSBuildExtensionsPath32: ...\MSBuild
            var msbExt32 = FirstNonEmpty(
                FromCli(cli, "msbuildextensionspath32"),
                FromCfg(cfg, "msbuild.MSBuildExtensionsPath32", baseDir, true),
                Environment.GetEnvironmentVariable("MSBuildExtensionsPath32"),
                !string.IsNullOrWhiteSpace(vsTools) ? Path.GetFullPath(Path.Combine(vsTools!, "..", "..")) : null
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

            // Opcjonalnie: TransformXml DLL – wylicz automatycznie
            var transformXml = FirstNonEmpty(
                FromCli(cli, "transformxml", "msbuild.transformxml"),
                FromCfg(cfg, "externalTasks.TransformXml", baseDir, true),
                !string.IsNullOrWhiteSpace(vsTools) ? Path.Combine(vsTools!, "Web", "Microsoft.Web.Publishing.Tasks.dll") : null,
                Environment.GetEnvironmentVariable("TRANSFORMXML_PATH")
            );

            if (!string.IsNullOrWhiteSpace(transformXml) && File.Exists(transformXml))
                Environment.SetEnvironmentVariable("TRANSFORMXML_PATH", transformXml);
        }
    }
}
