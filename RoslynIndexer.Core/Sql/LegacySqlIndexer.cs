// RoslynIndexer.Core/Sql/LegacySqlIndexer.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RoslynIndexer.Core.Logging;
using RoslynIndexer.Core.Sql.EfMigrations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RoslynIndexer.Core.Sql
{
    /// <summary>
    /// Exact legacy-compatible SQL/EF indexer.
    /// Writes: sql_bundle/graph/{nodes.csv,edges.csv,graph.json}, docs/bodies/*, manifest.json
    /// </summary>
    public static class LegacySqlIndexer
    {
        // Global dbGraph settings injected once from the main config.json (CLI layer).
        public static DbGraphConfig GlobalDbGraphConfig { get; set; } = DbGraphConfig.Empty;

        // Global migrations roots (optional) injected from the main config.json (CLI / Program layer).
        // When empty, AppendEfMigrationEdgesAndNodes falls back to codeRoots (legacy behaviour).
        public static string[] GlobalEfMigrationRoots { get; set; } = Array.Empty<string>();

        // ====== data rows (classes instead of records) ======
        private sealed class NodeRow
        {
            public string Key;
            public string Kind;
            public string Name;
            public string Schema;
            public string File;
            public int? Batch;
            public string Domain;
            public string BodyPath;

            public NodeRow(string key, string kind, string name, string schema, string file, int? batch, string domain, string bodyPath)
            {
                Key = key;
                Kind = kind;
                Name = name;
                Schema = schema;
                File = file;
                Batch = batch;
                Domain = domain;
                BodyPath = bodyPath;
            }
        }

        private sealed class EdgeRow
        {
            public string From;
            public string To;
            public string Relation;
            public string ToKind;
            public string File;
            public int? Batch;

            public EdgeRow(string from, string to, string relation, string toKind, string file, int? batch)
            {
                From = from;
                To = to;
                Relation = relation;
                ToKind = toKind;
                File = file;
                Batch = batch;
            }
        }

        /// <summary>
        /// Public entry point — identical signature and behavior as in the legacy tool.
        /// </summary>
        public static int Start(string outputDir, string sqlProjectRoot, string efRoot = "")
        {
            try
            {
                // Optional: load dbGraph config when LegacySqlIndexer is used directly
                // (e.g. in EntityBaseTypesTests). In CLI mode Program.Net9 already
                // sets GlobalDbGraphConfig from the main config.json – a separate
                // config.json in outputDir simply nie istnieje, więc niczego tu nie nadpiszemy.
                try
                {
                    var configPath = Path.Combine(outputDir, "config.json");
                    if (File.Exists(configPath))
                    {
                        var json = File.ReadAllText(configPath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var root = JObject.Parse(json);
                            GlobalDbGraphConfig = DbGraphConfig.FromJson(root);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Best-effort: brak lub zły config.json w outputDir nie może
                    // zabić całego procesu – po prostu zostajemy przy Empty.
                    ConsoleLog.Warn("[dbGraph] Failed to load dbGraph config from outputDir: " + ex.Message);
                }

                return RunBuild(outputDir, sqlProjectRoot, efRoot);
            }
            catch (Exception ex)
            {
                ConsoleLog.Error(ex.ToString());
                return 1;
            }
        }


        // ====== Build orchestration (legacy parity) ======
        private static int RunBuild(string outputDir, string sqlProjectRoot, string efRoot = "")
        {
            var outGraph = Path.Combine(outputDir, "graph");
            var outDocs = Path.Combine(outputDir, "docs");
            Directory.CreateDirectory(outGraph);
            Directory.CreateDirectory(outDocs);
            Directory.CreateDirectory(Path.Combine(outDocs, "bodies"));

            var sqlRoot = NormalizeDir(sqlProjectRoot);
            if (!Directory.Exists(sqlRoot))
            {
                ConsoleLog.Error("Provided sqlProjectRoot does not exist: " + sqlRoot);
                return 1;
            }

            ConsoleLog.Info("SQL root: " + sqlRoot);
            ConsoleLog.Info("Out dir : " + outputDir);

            // 1) SQL indexing
            var (nodes, edges, bodiesJsonlCount) = BuildSqlKnowledge(sqlRoot, outputDir);

            // 2) EF autodiscovery / explicit root
            List<string> codeRoots;
            if (!string.IsNullOrWhiteSpace(efRoot))
            {
                if (File.Exists(efRoot) && Path.GetExtension(efRoot).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                    efRoot = Path.GetDirectoryName(efRoot);

                var efDir = NormalizeDir(efRoot);

                if (!Directory.Exists(efDir))
                {
                    ConsoleLog.Warn("Provided efRoot does not exist: " + efDir + " — falling back to autodiscovery.");
                    codeRoots = AutoDiscoverCodeRoots(sqlRoot);
                }
                else if (IsDirectoryEmpty(efDir))
                {
                    ConsoleLog.Warn("Provided efRoot directory is empty: " + efDir + " — falling back to autodiscovery.");
                    codeRoots = AutoDiscoverCodeRoots(sqlRoot);
                }
                else
                {
                    codeRoots = new List<string> { efDir };
                }
            }
            else
            {
                codeRoots = AutoDiscoverCodeRoots(sqlRoot);
            }

            if (codeRoots.Count > 0)
            {
                ConsoleLog.Info("Code-roots (" + codeRoots.Count + "):");
                foreach (var cr in codeRoots)
                    ConsoleLog.Info("  - " + cr);

                // EF DbContext + raw SQL bridge
                AppendEfEdgesAndNodes(codeRoots, outputDir, nodes, edges);

                // NEW: EF migrations (FluentMigrator / DataMigration)
                AppendEfMigrationEdgesAndNodes(codeRoots, outputDir, nodes, edges);
            }
            else
            {
                ConsoleLog.Info("No EF code roots detected – EF stage skipped.");
            }

            // 3) Persist CSV/JSON + manifest
            WriteGraph(outputDir, nodes.Values, edges);
            WriteManifest(outputDir, sqlRoot, codeRoots, nodes.Count, edges.Count, bodiesJsonlCount);

            ConsoleLog.Info("SQL/EF graph build completed (nodes=" + nodes.Count
                 + ", edges=" + edges.Count
                 + ", bodies=" + bodiesJsonlCount + ").");
            return 0;
        }

        private static bool IsDirectoryEmpty(string path)
        {
            try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
            catch { return true; }
        }

        private static string NormalizeDir(string p)
        {
            var full = Path.GetFullPath(p);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        // ========================================================
        //  SQL indexing (legacy)
        // ========================================================
        private static readonly SqlScriptGenerator SqlGen =
            new Sql150ScriptGenerator(new SqlScriptGeneratorOptions { IncludeSemicolons = true });

        private static (ConcurrentDictionary<string, NodeRow>, List<EdgeRow>, int) BuildSqlKnowledge(string sqlRoot, string outDir)
        {
            ConsoleLog.Info("SQL stage: scanning .sql files…");
            var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Change Scripts","ChangeScripts","Initial Data","InitialData",
                "Snapshots","Tools",".git",".vs","bin","obj"
            };

            IEnumerable<string> EnumerateSql(string dir)
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (ignore.Contains(name)) continue;
                    foreach (var p in EnumerateSql(d))
                        yield return p;
                }

                foreach (var f in Directory.EnumerateFiles(dir, "*.sql"))
                    yield return f;
            }

            var files = EnumerateSql(sqlRoot).ToArray();
            ConsoleLog.Info("Found SQL files: " + files.Length);

            var nodes = new ConcurrentDictionary<string, NodeRow>(StringComparer.OrdinalIgnoreCase);
            var edges = new ConcurrentBag<EdgeRow>();
            var definedKinds = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var parser = new TSql150Parser(true);
            var bodiesDir = Path.Combine(outDir, "docs", "bodies");
            var bodiesJsonl = Path.Combine(outDir, "docs", "sql_bodies.jsonl");
            Directory.CreateDirectory(bodiesDir);
            int bodiesCount = 0;

            using (var bodiesWriter = new StreamWriter(bodiesJsonl, false, new UTF8Encoding(false)))
            {
                files
                    .AsParallel()
                    .WithDegreeOfParallelism(Environment.ProcessorCount)
                    .ForAll(path =>
                    {
                        try
                        {
                            var sql = ReadAndPreprocess(path);
                            IList<ParseError> errors;
                            using (var sr = new StringReader(sql))
                            {
                                var fragment = parser.Parse(sr, out errors);
                                if (errors != null && errors.Count > 0)
                                {
                                    var msg = string.Join("; ",
                                        errors.Take(2).Select(e => e.Message + " (L" + e.Line + ",C" + e.Column + ")"));
                                    ConsoleLog.Warn("[" + Path.GetFileName(path) + "] parser errors: " + msg);
                                }
                                    
                                var script = fragment as TSqlScript;
                                if (script == null) return;
                                var domain = DeriveDomain(sqlRoot, path);
                                var isPrePost = IsPreOrPostDeployment(path);

                                for (int bi = 0; bi < script.Batches.Count; bi++)
                                {
                                    var batch = script.Batches[bi];
                                    var collector = new RefCollector();
                                    batch.Accept(collector);

                                    List<Tuple<SchemaObjectName, string, TSqlFragment>> defines;
                                    if (collector.Defines.Count > 0)
                                    {
                                        defines = collector.Defines;
                                    }
                                    else
                                    {
                                        var pseudo = MakePseudoDefine(path, bi, isPrePost ? "DEPLOY" : "BATCH");
                                        defines = new List<Tuple<SchemaObjectName, string, TSqlFragment>> { pseudo };
                                    }

                                    foreach (var def in defines)
                                    {
                                        var key = Key(def.Item1, def.Item2);
                                        var schema = def.Item1.SchemaIdentifier != null
                                            ? def.Item1.SchemaIdentifier.Value
                                            : "dbo";
                                        var name = def.Item1.BaseIdentifier != null
                                            ? def.Item1.BaseIdentifier.Value
                                            : "(anon)";

                                        string bodyRelPath = null;
                                        if (IsBodyKind(def.Item2))
                                        {
                                            var fileName = schema + "." + name + "." + def.Item2 + ".sql";
                                            var bodyAbs = Path.Combine(bodiesDir, fileName);
                                            string scripted;
                                            lock (SqlGen)
                                            {
                                                SqlGen.GenerateScript(def.Item3, out scripted);
                                                File.WriteAllText(bodyAbs, scripted, new UTF8Encoding(false));
                                            }

                                            bodyRelPath = "docs/bodies/" + fileName;

                                            lock (bodiesWriter)
                                            {
                                                var obj = new
                                                {
                                                    key,
                                                    kind = def.Item2,
                                                    schema,
                                                    name,
                                                    domain,
                                                    file = path,
                                                    body = File.ReadAllText(bodyAbs, Encoding.UTF8)
                                                };
                                                bodiesWriter.WriteLine(JsonConvert.SerializeObject(obj));
                                                bodiesCount++;
                                            }
                                        }

                                        var node = new NodeRow(key, def.Item2, name, schema, path, bi, domain, bodyRelPath);
                                        nodes.TryAdd(key, node);
                                        definedKinds.TryAdd(NameKey(def.Item1), def.Item2);

                                        foreach (var r in collector.References)
                                        {
                                            var toKeyBase = NameKey(r.Item1);
                                            var toKind = r.Item2 ?? "UNKNOWN";
                                            edges.Add(new EdgeRow(
                                                from: key,
                                                to: toKeyBase + "|" + toKind,
                                                relation: r.Item3 ?? "Uses",
                                                toKind: toKind,
                                                file: path,
                                                batch: bi
                                            ));
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ConsoleLog.Error("[SQL:" + Path.GetFileName(path) + "] " + ex.Message);
                        }
                    });
            }

            // resolve kinds on edges + dedupe
            var fixedEdges = new List<EdgeRow>(edges.Count);
            foreach (var e in edges)
            {
                var split = SplitToNameAndKind(e.To);
                var baseName = split.Item1;
                var _kind = split.Item2;

                if (definedKinds.TryGetValue(baseName, out var realKind))
                {
                    fixedEdges.Add(new EdgeRow(
                        e.From,
                        baseName + "|" + realKind,
                        e.Relation,
                        realKind,
                        e.File,
                        e.Batch));
                }
                else
                {
                    fixedEdges.Add(e);
                }
            }

            fixedEdges = fixedEdges
                .GroupBy(x => x.From + "|" + x.To + "|" + x.Relation)
                .Select(g => g.First())
                .ToList();

            // ensure missing target nodes exist
            foreach (var e in fixedEdges)
            {
                if (!nodes.ContainsKey(e.To))
                {
                    var pair = SplitToNameAndKind(e.To);
                    var baseName = pair.Item1;
                    var kind = pair.Item2;
                    var sn = SplitSchemaAndName(baseName);
                    var schema = sn.Item1;
                    var name = sn.Item2;

                    nodes.TryAdd(
                        e.To,
                        new NodeRow(e.To, kind, name, schema, null, null, "(external)", null));
                }
            }

            ConsoleLog.Info("SQL stage finished: nodes=" + nodes.Count + ", edges=" + fixedEdges.Count + ", bodies=" + bodiesCount);
            return (nodes, fixedEdges, bodiesCount);
        }

        private static bool IsBodyKind(string kind)
            => kind == "PROC" || kind == "FUNC" || kind == "VIEW" ||
               kind == "TRIGGER" || kind == "TABLE" || kind == "SEQUENCE" || kind == "TYPE";

        private static string ReadAndPreprocess(string path)
        {
            var text = File.ReadAllText(path);
            var sb = new StringBuilder();

            foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (Regex.IsMatch(raw, @"^\s*:(r|setvar|connect|on\s+error\s+exit)\b",
                        RegexOptions.IgnoreCase))
                    continue;

                sb.AppendLine(raw);
            }

            var cleaned = sb.ToString();
            cleaned = Regex.Replace(cleaned, @"\$\([^)]+\)", "0");
            return cleaned;
        }

        private static string DeriveDomain(string root, string file)
        {
            var rel = GetRelativePathSafe(root, file);
            var parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[0] : string.Empty;
        }

        private static bool IsPreOrPostDeployment(string p)
        {
            var name = Path.GetFileName(p);
            return name.Equals("PreDeployment.sql", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("PostDeployment.sql", StringComparison.OrdinalIgnoreCase);
        }

        private static Tuple<string, string> SplitToNameAndKind(string key)
        {
            var i = key.LastIndexOf('|');
            if (i >= 0)
                return Tuple.Create(key.Substring(0, i), key.Substring(i + 1));

            return Tuple.Create(key, "UNKNOWN");
        }

        private static Tuple<string, string> SplitSchemaAndName(string baseName)
        {
            var parts = baseName.Split('.');
            if (parts.Length == 3) return Tuple.Create(parts[1], parts[2]);
            if (parts.Length == 2) return Tuple.Create(parts[0], parts[1]);
            return Tuple.Create("dbo", parts[parts.Length - 1]);
        }

        private static List<string> AutoDiscoverCodeRoots(string sqlRoot)
        {
            var results = new List<string>();

            DirectoryInfo parent;
            try
            {
                parent = Directory.GetParent(sqlRoot);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException)
            {
                ConsoleLog.Warn("AutoDiscoverCodeRoots: invalid sqlRoot '" + sqlRoot + "': " + ex.Message);
                return results;
            }

            if (parent == null)
                return results;

            var p2 = parent.Parent ?? parent;
            var p3 = p2.Parent ?? p2;
            var src = p3.FullName;

            var preferred = Path.Combine(src, "Server", "DataAccess");
            var bases = Directory.Exists(preferred)
                ? new[] { preferred }
                : new[] { src };

            string[] excludes = { "test", "tests", "example", "examples", "sample", "samples", "dev", "client", "tools" };

            bool IsExcluded(string path) =>
                path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Any(seg => excludes.Any(x =>
                        string.Equals(seg, x, StringComparison.OrdinalIgnoreCase)));

            string[] csproj;
            try
            {
                csproj = bases
                    .Where(Directory.Exists)
                    .SelectMany(b => Directory.EnumerateFiles(b, "*.csproj", SearchOption.AllDirectories))
                    .Where(p => p.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                                p.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) < 0 &&
                                p.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                                p.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) < 0)
                    .Where(p => !IsExcluded(p))
                    .ToArray();
            }
            catch (UnauthorizedAccessException ex)
            {
                ConsoleLog.Warn("AutoDiscoverCodeRoots: unauthorized while enumerating *.csproj under "
                    + string.Join(";", bases) + ": " + ex.Message);
                csproj = Array.Empty<string>();
            }
            catch (IOException ex)
            {
                ConsoleLog.Warn("AutoDiscoverCodeRoots: IO error while enumerating *.csproj under "
                    + string.Join(";", bases) + ": " + ex.Message);
                csproj = Array.Empty<string>();
            }

            bool LooksLikeEfDir(string dir)
            {
                string[] files;
                try
                {
                    files = Directory
                        .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                        .Take(300)
                        .ToArray();
                }
                catch (UnauthorizedAccessException ex)
                {
                    ConsoleLog.Warn("AutoDiscoverCodeRoots: unauthorized while enumerating *.cs under " + dir + ": " + ex.Message);
                    return false;
                }
                catch (IOException ex)
                {
                    ConsoleLog.Warn("AutoDiscoverCodeRoots: IO error while enumerating *.cs under " + dir + ": " + ex.Message);
                    return false;
                }

                foreach (var f in files)
                {
                    string txt;
                    try
                    {
                        txt = File.ReadAllText(f);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        ConsoleLog.Warn("AutoDiscoverCodeRoots: unauthorized while reading " + f + ": " + ex.Message);
                        continue;
                    }
                    catch (IOException ex)
                    {
                        ConsoleLog.Warn("AutoDiscoverCodeRoots: IO error while reading " + f + ": " + ex.Message);
                        continue;
                    }

                    if (Regex.IsMatch(txt, @"class\s+\w+\s*:\s*(\w+\.)*(Identity)?DbContext\b"))
                        return true;

                    if (txt.Contains(" DbSet<") || txt.Contains("\tDbSet<"))
                        return true;
                }

                return false;
            }

            foreach (var proj in csproj)
            {
                var dir = Path.GetDirectoryName(proj);
                if (string.IsNullOrEmpty(dir))
                    continue;

                try
                {
                    if (LooksLikeEfDir(dir))
                        results.Add(dir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    ConsoleLog.Warn("AutoDiscoverCodeRoots: error while scanning directory " + dir + ": " + ex.Message);
                }
            }

            return results;
        }


 

        private static string NameKey(SchemaObjectName n)
        {
            var db = n.DatabaseIdentifier != null ? n.DatabaseIdentifier.Value : null;
            var schema = n.SchemaIdentifier != null ? n.SchemaIdentifier.Value : "dbo";
            var name = n.BaseIdentifier != null ? n.BaseIdentifier.Value : "(anon)";
            return (db != null ? db + "." : "") + schema + "." + name;
        }

        private static string Key(SchemaObjectName n, string kind) => NameKey(n) + "|" + kind;

        private static Tuple<SchemaObjectName, string, TSqlFragment> MakePseudoDefine(string file, int bi, string kind)
        {
            var n = new SchemaObjectName();
            n.Identifiers.Add(new Identifier { Value = "dbo" });
            n.Identifiers.Add(new Identifier
            {
                Value = "__" + kind.ToLowerInvariant() + "__:" + Path.GetFileName(file) + ":" + bi
            });
            return Tuple.Create(n, kind, (TSqlFragment)new TSqlScript());
        }

        // ====== Visitor (legacy) ======
        private sealed class RefCollector : TSqlFragmentVisitor
        {
            public readonly List<Tuple<SchemaObjectName, string, string>> References =
                new List<Tuple<SchemaObjectName, string, string>>();

            public readonly List<Tuple<SchemaObjectName, string, TSqlFragment>> Defines =
                new List<Tuple<SchemaObjectName, string, TSqlFragment>>();

            private bool IsTempOrVar(SchemaObjectName n)
            {
                var bi = n.BaseIdentifier != null ? n.BaseIdentifier.Value : null;
                if (bi == null) return false;
                return bi.StartsWith("#") || bi.StartsWith("@");
            }

            private void AddDefine(SchemaObjectName name, string kind, TSqlFragment frag)
            {
                if (name == null || name.BaseIdentifier == null) return;
                Defines.Add(Tuple.Create(name, kind, frag));
            }

            private void AddRef(SchemaObjectName name, string kindHint, string relation)
            {
                if (name == null || name.BaseIdentifier == null) return;
                if (IsTempOrVar(name)) return;
                References.Add(Tuple.Create(name, kindHint, relation));
            }

            // Definitions
            public override void Visit(CreateTableStatement node) =>
                AddDefine(node.SchemaObjectName, "TABLE", node);

            public override void Visit(CreateViewStatement node) =>
                AddDefine(node.SchemaObjectName, "VIEW", node);

            public override void Visit(CreateProcedureStatement node)
            {
                if (node.ProcedureReference != null)
                    AddDefine(node.ProcedureReference.Name, "PROC", node);
            }

            public override void Visit(CreateFunctionStatement node) =>
                AddDefine(node.Name, "FUNC", node);

            public override void Visit(CreateTypeTableStatement node) =>
                AddDefine(node.Name, "TYPE", node);

            public override void Visit(CreateTypeUddtStatement node) =>
                AddDefine(node.Name, "TYPE", node);

            public override void Visit(CreateSequenceStatement node) =>
                AddDefine(node.Name, "SEQUENCE", node);

            public override void ExplicitVisit(CreateSynonymStatement node)
            {
                AddDefine(node.Name, "SYNONYM", node);
                if (node.ForName != null)
                    AddRef(node.ForName, null, "SynonymFor");
            }

            public override void Visit(CreateTriggerStatement node)
            {
                AddDefine(node.Name, "TRIGGER", node);
                var trgObj = node.TriggerObject;
                if (trgObj != null && trgObj.Name != null)
                    AddRef(trgObj.Name, null, "On");
            }

            // ALTER statements
            public override void Visit(AlterTableAddTableElementStatement node) =>
                AddDefine(node.SchemaObjectName, "TABLE", node);

            public override void Visit(AlterViewStatement node)
            {
                if (node.SchemaObjectName != null)
                    AddDefine(node.SchemaObjectName, "VIEW", node);
            }

            public override void Visit(AlterProcedureStatement node)
            {
                if (node.ProcedureReference != null && node.ProcedureReference.Name != null)
                    AddDefine(node.ProcedureReference.Name, "PROC", node);
            }

            public override void Visit(AlterFunctionStatement node)
            {
                if (node.Name != null)
                    AddDefine(node.Name, "FUNC", node);
            }

            // References
            public override void Visit(NamedTableReference node)
            {
                if (node.SchemaObject != null)
                    AddRef(node.SchemaObject, "TABLE_OR_VIEW", "ReadsFrom");
            }

            public override void Visit(InsertStatement node)
            {
                if (node.InsertSpecification != null &&
                    node.InsertSpecification.Target is NamedTableReference t)
                    AddRef(t.SchemaObject, "TABLE", "WritesTo");
            }

            public override void Visit(UpdateStatement node)
            {
                if (node.UpdateSpecification != null &&
                    node.UpdateSpecification.Target is NamedTableReference t)
                    AddRef(t.SchemaObject, "TABLE", "WritesTo");
            }

            public override void Visit(DeleteStatement node)
            {
                if (node.DeleteSpecification != null &&
                    node.DeleteSpecification.Target is NamedTableReference t)
                    AddRef(t.SchemaObject, "TABLE", "WritesTo");
            }

            public override void Visit(MergeStatement node)
            {
                var ms = node.MergeSpecification;
                if (ms == null) return;

                var t = ms.Target as NamedTableReference;
                if (t != null)
                    AddRef(t.SchemaObject, "TABLE", "WritesTo");

                var s = ms.TableReference as NamedTableReference;
                if (s != null)
                    AddRef(s.SchemaObject, "TABLE_OR_VIEW", "ReadsFrom");
            }

            public override void Visit(ExecuteSpecification node)
            {
                var pr = node.ExecutableEntity as ExecutableProcedureReference;
                if (pr != null &&
                    pr.ProcedureReference != null &&
                    pr.ProcedureReference.ProcedureReference != null &&
                    pr.ProcedureReference.ProcedureReference.Name != null)
                {
                    var nm = pr.ProcedureReference.ProcedureReference.Name;
                    AddRef(nm, "PROC", "Executes");
                }
            }
        }

       


        private static void AppendEfEdgesAndNodes(
            IEnumerable<string> codeRoots,
            string outDir,
            ConcurrentDictionary<string, NodeRow> nodes,
            List<EdgeRow> edges)
        {
            ConsoleLog.Info("EF stage: scanning C# files (mappings, DbSet, raw SQL, POCO entities).");

            var csFiles = codeRoots
                .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
                .ToArray();

            ConsoleLog.Info($"C# files found: {csFiles.Length}");

            var trees = new List<SyntaxTree>();
            foreach (var f in csFiles)
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f));

            // 1) entity -> (schema, table) from EF mappings (if any)
            var entityMap = ExtractEntityMappings(trees);
            ConsoleLog.Info($"[EF] entityMap entries: {entityMap.Count}");

            // 1a) TABLE nodes from previous SQL stage (if any)
            var tableNodes = nodes.Values
                .Where(n => string.Equals(n.Kind, "TABLE", StringComparison.OrdinalIgnoreCase))
                .ToList();
            ConsoleLog.Info($"[EF] TABLE nodes available before EF stage: {tableNodes.Count}");

            // 2) dbGraph settings come ONLY from GlobalDbGraphConfig (set by CLI from main config.json)
            var dbGraphCfg = GlobalDbGraphConfig ?? DbGraphConfig.Empty;

            if (dbGraphCfg.HasEntityBaseTypes)
                ConsoleLog.Info($"[dbGraph] entityBaseTypes: {string.Join(", ", dbGraphCfg.EntityBaseTypes)}");
            else
                ConsoleLog.Info("[dbGraph] entityBaseTypes: <none> (POCO detection off)");

            // 3) Classic DbSet<T> -> TABLE (MapsTo) using entityMap only
            foreach (var t in trees)
            {
                var root = t.GetRoot();
                foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                {
                    var typeText = prop.Type.ToString();
                    if (!typeText.StartsWith("DbSet<") && !typeText.StartsWith("IDbSet<"))
                        continue;

                    var parts = typeText.Split('<', '>');
                    var typeName = parts.Length > 1 ? parts[1] : typeText;
                    if (!entityMap.TryGetValue(typeName, out var map))
                        continue;

                    var clsDecl = prop.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                    var cls = clsDecl != null ? clsDecl.Identifier.Text : "UnknownClass";
                    var from = "csharp:" + cls + "." + prop.Identifier.Text + "|DBSET";
                    var toKey = map.Item1 + "." + map.Item2 + "|TABLE";

                    edges.Add(new EdgeRow(
                        from,
                        toKey,
                        "MapsTo",
                        "TABLE",
                        t.FilePath,
                        null));

                    nodes.TryAdd(
                        from,
                        new NodeRow(from, "DBSET", prop.Identifier.Text, "csharp", t.FilePath, null, "code", null));
                }
            }

            // 4) entityBaseTypes detection -> ENTITY (+ optional MapsTo TABLE)
            // Encja powstaje ZAWSZE, jeśli dziedziczy po typie z configu.
            // Mapowanie do TABLE jest "best effort": jeśli znajdziemy tabelę, dokładamy edge.
            if (dbGraphCfg.HasEntityBaseTypes)
            {
                foreach (var t in trees)
                {
                    var root = t.GetRoot();
                    foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                    {
                        var baseTypeSyntax = cls.BaseList?.Types.FirstOrDefault();
                        if (baseTypeSyntax == null)
                            continue;

                        var baseTypeText = baseTypeSyntax.ToString();
                        if (string.IsNullOrWhiteSpace(baseTypeText))
                            continue;

                        var baseTypeSimple = baseTypeText.Split('.').Last();

                        Console.WriteLine($"[ENTITY-DEBUG] Class='{cls.Identifier.Text}', BaseSyntax='{baseTypeText}'");

                        bool matchesBase = dbGraphCfg.EntityBaseTypes.Any(cfgType =>
                        {
                            if (string.IsNullOrWhiteSpace(cfgType))
                                return false;

                            var cfgSimple = cfgType.Split('.').Last();

                            return string.Equals(baseTypeText, cfgType, StringComparison.Ordinal)
                                   || string.Equals(baseTypeText, cfgSimple, StringComparison.Ordinal)
                                   || string.Equals(baseTypeSimple, cfgSimple, StringComparison.Ordinal);
                        });

                        if (!matchesBase)
                        {
                            Console.WriteLine($"[ENTITY-DEBUG]   -> Base DOES NOT match config for '{cls.Identifier.Text}' (base='{baseTypeText}')");
                            continue;
                        }

                        Console.WriteLine($"[ENTITY-DEBUG]   -> Base MATCHES config for '{cls.Identifier.Text}' (base='{baseTypeText}')");

                        var entityName = cls.Identifier.Text;
                        var entityKey = $"csharp:{entityName}|ENTITY";

                        // 4a) ZAWSZE dodajemy ENTITY node
                        nodes.TryAdd(
                            entityKey,
                            new NodeRow(
                                entityKey,
                                "ENTITY",
                                entityName,
                                "csharp",
                                t.FilePath,
                                null,
                                "code",
                                null));

                        Console.WriteLine($"[ENTITY-DEBUG]   -> ENTITY node added for '{entityName}' ({entityKey})");

                        // 4b) Best-effort: spróbuj znaleźć / utworzyć TABLE, żeby dodać MapsTo
                        bool mapFound = false;
                        string schema = "dbo";  // default EF schema
                        string tableName = entityName;

                        // 4b.1: entityMap (Fluent / EF mappings)
                        if (entityMap.TryGetValue(entityName, out var map))
                        {
                            schema = map.Item1;
                            tableName = map.Item2;
                            mapFound = true;
                            Console.WriteLine($"[ENTITY-DEBUG]   -> Using entityMap direct for '{entityName}': {schema}.{tableName}");
                        }
                        else
                        {
                            var foundKey = entityMap.Keys.FirstOrDefault(k =>
                                string.Equals(k, entityName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(k, entityName + "s", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(k + "s", entityName, StringComparison.OrdinalIgnoreCase));

                            if (foundKey != null)
                            {
                                var m = entityMap[foundKey];
                                schema = m.Item1;
                                tableName = m.Item2;
                                mapFound = true;
                                Console.WriteLine($"[ENTITY-DEBUG]   -> Using entityMap heuristic for '{entityName}': key='{foundKey}', table={schema}.{tableName}");
                            }
                        }

                        // 4b.2: [Table] attribute on the class
                        if (!mapFound)
                        {
                            var tableAttr = cls.AttributeLists
                                .SelectMany(al => al.Attributes)
                                .FirstOrDefault(a =>
                                {
                                    var n = a.Name.ToString();
                                    return n == "Table"
                                           || n == "TableAttribute"
                                           || n.EndsWith(".Table")
                                           || n.EndsWith(".TableAttribute");
                                });

                            if (tableAttr != null && tableAttr.ArgumentList != null)
                            {
                                foreach (var arg in tableAttr.ArgumentList.Arguments)
                                {
                                    // Positional: table name
                                    if (arg.NameEquals == null &&
                                        arg.Expression is LiteralExpressionSyntax lit &&
                                        lit.IsKind(SyntaxKind.StringLiteralExpression))
                                    {
                                        tableName = lit.Token.ValueText;
                                    }
                                    // Named: Schema = "..."
                                    else if (arg.NameEquals != null &&
                                             arg.NameEquals.Name.Identifier.Text == "Schema" &&
                                             arg.Expression is LiteralExpressionSyntax schemaLit &&
                                             schemaLit.IsKind(SyntaxKind.StringLiteralExpression))
                                    {
                                        schema = schemaLit.Token.ValueText;
                                    }
                                }

                                mapFound = true;
                                Console.WriteLine($"[ENTITY-DEBUG]   -> Using [Table] attribute for '{entityName}': {schema}.{tableName}");
                            }
                        }

                        // 4b.3: fallback – istniejące TABLE z SQL (jeśli są)
                        if (!mapFound && tableNodes.Count > 0)
                        {
                            var tableNode = tableNodes.FirstOrDefault(n =>
                                string.Equals(n.Name, entityName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(n.Name, entityName + "s", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(n.Name + "s", entityName, StringComparison.OrdinalIgnoreCase));

                            if (tableNode != null)
                            {
                                schema = string.IsNullOrEmpty(tableNode.Schema) ? "dbo" : tableNode.Schema;
                                tableName = tableNode.Name;
                                mapFound = true;
                                Console.WriteLine($"[ENTITY-DEBUG]   -> Using TABLE node fallback for '{entityName}': {schema}.{tableName}");
                            }
                        }

                        // 4c) Jeśli mimo wszystko nie ma TABLE – zostawiamy samą ENTITY
                        if (!mapFound)
                        {
                            Console.WriteLine($"[ENTITY-DEBUG]   -> NO TABLE mapping for '{entityName}' – ENTITY without MapsTo.");
                            continue;
                        }

                        var tableKey = $"{schema}.{tableName}|TABLE";

                        // Upewniamy się, że TABLE node istnieje (jeśli już był z SQL – TryAdd pozostawi istniejący).
                        nodes.TryAdd(
                            tableKey,
                            new NodeRow(
                                tableKey,
                                "TABLE",
                                tableName,
                                schema,
                                t.FilePath,
                                null,
                                "ef",
                                null));

                        edges.Add(new EdgeRow(
                            entityKey,
                            tableKey,
                            "MapsTo",
                            "TABLE",
                            t.FilePath,
                            null));

                        Console.WriteLine($"[ENTITY-DEBUG]   -> ENTITY + MapsTo added for '{entityName}' => {tableKey}");
                    }
                }
            }
        }

        private static void AppendEfMigrationEdgesAndNodes(
         IEnumerable<string> codeRoots,
         string outDir,
         ConcurrentDictionary<string, NodeRow> nodes,
         List<EdgeRow> edges)
        {
            ConsoleLog.Info("EF-Migrations (v2): Roslyn-based EfMigrationAnalyzer…");

            // 1) Collect all roots to scan:
            //    - explicit GlobalEfMigrationRoots from config.json,
            //    - plus codeRoots (for backward compatibility), without duplicates.
            var effectiveRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (GlobalEfMigrationRoots != null)
            {
                foreach (var raw in GlobalEfMigrationRoots)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    var dir = NormalizeDir(raw);
                    if (!Directory.Exists(dir))
                    {
                        ConsoleLog.Warn("EF-Migrations: directory from GlobalEfMigrationRoots does not exist: " + dir);
                        continue;
                    }

                    effectiveRoots.Add(dir);
                }
            }

            var codeRootsArray = (codeRoots ?? Array.Empty<string>()).ToArray();

            if (effectiveRoots.Count == 0)
            {
                // No explicit config – fall back to legacy behaviour: scan codeRoots only.
                foreach (var raw in codeRootsArray)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    var dir = NormalizeDir(raw);
                    if (!Directory.Exists(dir))
                    {
                        ConsoleLog.Warn("EF-Migrations: directory does not exist: " + dir);
                        continue;
                    }

                    effectiveRoots.Add(dir);
                }
            }
            else
            {
                // We *do* have explicit migration roots – still allow migrations living next to codeRoots.
                foreach (var raw in codeRootsArray)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    var dir = NormalizeDir(raw);
                    if (Directory.Exists(dir))
                        effectiveRoots.Add(dir);
                }
            }

            if (effectiveRoots.Count == 0)
            {
                ConsoleLog.Info("EF-Migrations (v2): no migration roots configured/found – skipped.");
                return;
            }

            var analyzer = new EfMigrationAnalyzer();
            int addedMigrations = 0;
            int addedEdges = 0;

            // 2) Primary path: use EfMigrationAnalyzer (for real projects like nopCommerce).
            foreach (var root in effectiveRoots)
            {
                try
                {
                    var infos = analyzer.Analyze(root, repoRoot: string.Empty, cancellationToken: CancellationToken.None);
                    if (infos == null || infos.Count == 0)
                        continue;

                    foreach (var mig in infos)
                    {
                        if (mig == null || string.IsNullOrEmpty(mig.ClassName))
                            continue;

                        var key = "csharp:" + mig.ClassName + "|MIGRATION";

                        // Migration node
                        nodes.TryAdd(
                            key,
                            new NodeRow(
                                key: key,
                                kind: "MIGRATION",
                                name: mig.ClassName,
                                schema: "csharp",
                                file: mig.FileRelativePath ?? string.Empty,
                                batch: null,
                                domain: "code",
                                bodyPath: null));

                        addedMigrations++;

                        // Edges: use UpOperations (schema evolution "forward")
                        foreach (var op in mig.UpOperations ?? Array.Empty<EfMigrationOperation>())
                        {
                            string relation;
                            switch (op.Kind)
                            {
                                case EfMigrationOperationKind.CreateTable:
                                case EfMigrationOperationKind.DropTable:
                                case EfMigrationOperationKind.CreateIndex:
                                case EfMigrationOperationKind.DropIndex:
                                case EfMigrationOperationKind.TouchTable:
                                    relation = "SchemaChange";
                                    break;

                                case EfMigrationOperationKind.RawSql:
                                case EfMigrationOperationKind.Unknown:
                                default:
                                    // Raw SQL is preserved but we do not guess affected tables.
                                    continue;
                            }

                            var tableName = op.Table;
                            if (string.IsNullOrWhiteSpace(tableName))
                                continue;

                            // For now we default to dbo – SQL side may refine this later.
                            var toKey = "dbo." + tableName + "|TABLE";

                            edges.Add(new EdgeRow(
                                from: key,
                                to: toKey,
                                relation: relation,
                                toKind: "TABLE",
                                file: mig.FileRelativePath ?? string.Empty,
                                batch: null));

                            addedEdges++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLog.Warn("EF-Migrations (v2): failed to analyze root '" + root + "': " + ex.Message);
                }
            }

            // 3) Fallback path: for tiny test scaffolds (like MiniEf) where EfMigrationAnalyzer sees nothing,
            //    we scan *.cs directly and look for *Migration classes + Schema.Table(...) calls.
            if (addedMigrations == 0)
            {
                int fallbackMigrations = 0;
                int fallbackEdges = 0;

                foreach (var root in effectiveRoots)
                {
                    IEnumerable<string> csFiles;
                    try
                    {
                        csFiles = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                    {
                        ConsoleLog.Warn("EF-Migrations (fallback): cannot enumerate *.cs under " + root + ": " + ex.Message);
                        continue;
                    }

                    foreach (var file in csFiles)
                    {
                        string code;
                        try
                        {
                            code = File.ReadAllText(file);
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                        {
                            ConsoleLog.Warn("EF-Migrations (fallback): cannot read " + file + ": " + ex.Message);
                            continue;
                        }

                        // Find classes ending with 'Migration'
                        var classMatches = Regex.Matches(code, @"class\s+(\w*Migration)\b");
                        if (classMatches.Count == 0)
                            continue;

                        // Extract table names from Schema.Table(...) calls.
                        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        // Schema.Table(nameof(Customer))
                        var nameofMatches = Regex.Matches(code, @"Schema\.Table\(\s*nameof\(\s*(\w+)\s*\)\s*\)");
                        foreach (Match m in nameofMatches)
                        {
                            var name = m.Groups[1].Value;
                            if (!string.IsNullOrWhiteSpace(name))
                                tableNames.Add(name);
                        }

                        // Schema.Table("Customer")
                        var stringMatches = Regex.Matches(code, @"Schema\.Table\(\s*""([^""]+)""\s*\)");
                        foreach (Match m in stringMatches)
                        {
                            var name = m.Groups[1].Value;
                            if (!string.IsNullOrWhiteSpace(name))
                                tableNames.Add(name);
                        }

                        foreach (Match m in classMatches)
                        {
                            var className = m.Groups[1].Value;
                            var migKey = "csharp:" + className + "|MIGRATION";

                            nodes.TryAdd(
                                migKey,
                                new NodeRow(
                                    key: migKey,
                                    kind: "MIGRATION",
                                    name: className,
                                    schema: "csharp",
                                    file: file,
                                    batch: null,
                                    domain: "code",
                                    bodyPath: null));

                            fallbackMigrations++;

                            foreach (var tableName in tableNames)
                            {
                                var tableKey = "dbo." + tableName + "|TABLE";

                                edges.Add(new EdgeRow(
                                    from: migKey,
                                    to: tableKey,
                                    relation: "SchemaChange",
                                    toKind: "TABLE",
                                    file: file,
                                    batch: null));

                                fallbackEdges++;
                            }
                        }
                    }
                }

                if (fallbackMigrations > 0)
                {
                    addedMigrations += fallbackMigrations;
                    addedEdges += fallbackEdges;
                    ConsoleLog.Info(
                        "EF-Migrations (fallback) added: migrations=" + fallbackMigrations +
                        ", edges=" + fallbackEdges);
                }
            }

            ConsoleLog.Info(
                "EF-Migrations (v2) finished: migrations=" + addedMigrations +
                ", edges(Up)=" + addedEdges +
                ", total nodes=" + nodes.Count +
                ", total edges=" + edges.Count);
        }



        private static Dictionary<string, Tuple<string, string>> ExtractEntityMappings(List<SyntaxTree> trees)
        {
            var map = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in trees)
            {
                var root = t.GetRoot();

                // [Table("Name", Schema="dbo")]
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var attr = cls.AttributeLists
                        .SelectMany(a => a.Attributes)
                        .FirstOrDefault(a =>
                            a.Name.ToString().EndsWith("Table") ||
                            a.Name.ToString().EndsWith("TableAttribute"));

                    if (attr != null)
                    {
                        var nameArgSyntax =
                            attr.ArgumentList != null && attr.ArgumentList.Arguments.Count > 0
                                ? attr.ArgumentList.Arguments[0]
                                : null;

                        var nameArg = nameArgSyntax != null
                            ? nameArgSyntax.ToString().Trim('"', '\'')
                            : null;

                        var schema = "dbo";
                        var schemaArg = attr.ArgumentList != null
                            ? attr.ArgumentList.Arguments.FirstOrDefault(a =>
                                a.ToString().StartsWith("Schema", StringComparison.OrdinalIgnoreCase))
                            : null;

                        if (schemaArg != null)
                        {
                            var s = schemaArg.ToString();
                            var idx = s.IndexOf('=');
                            if (idx > 0)
                                schema = s.Substring(idx + 1).Trim().Trim('"', '\'');
                        }

                        if (!string.IsNullOrEmpty(nameArg))
                            map[FullName(cls)] = Tuple.Create(schema, nameArg);
                    }
                }

                // Fluent API: modelBuilder.Entity<T>().ToTable("Name","Schema")
                foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var ma = inv.Expression as MemberAccessExpressionSyntax;
                    if (ma != null &&
                        ma.Name.Identifier.Text == "ToTable" &&
                        inv.ArgumentList != null &&
                        inv.ArgumentList.Arguments.Count >= 1)
                    {
                        var args = inv.ArgumentList.Arguments;
                        var name = args[0].ToString().Trim('"', '\'');
                        var schema = args.Count >= 2
                            ? args[1].ToString().Trim('"', '\'')
                            : "dbo";

                        var ent = ma.Expression as InvocationExpressionSyntax;
                        var ma2 = ent != null ? ent.Expression as MemberAccessExpressionSyntax : null;
                        if (ma2 != null &&
                            ma2.Name.Identifier.Text == "Entity" &&
                            ent.ArgumentList != null &&
                            ent.ArgumentList.Arguments.Count == 1)
                        {
                            var arg = ent.ArgumentList.Arguments[0].ToString();
                            string type;
                            if (arg.Contains("typeof"))
                            {
                                var open = arg.IndexOf('(');
                                var close = arg.IndexOf(')');
                                if (open >= 0 && close > open)
                                    type = arg.Substring(open + 1, close - open - 1);
                                else
                                    type = arg;
                            }
                            else
                            {
                                type = arg.Trim('<', '>');
                            }

                            map[type] = Tuple.Create(schema, name);
                        }
                    }
                }
            }

            return map;
        }

        private static string FullName(ClassDeclarationSyntax cls)
        {
            var nsDecl = cls.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
            var ns = nsDecl != null ? nsDecl.Name.ToString() : null;
            return string.IsNullOrEmpty(ns) ? cls.Identifier.Text : ns + "." + cls.Identifier.Text;
        }

        // ====== EF raw-SQL mini collector ======
        private sealed class MiniSqlRefCollector : TSqlFragmentVisitor
        {
            public readonly List<Tuple<string, string, string, string>> Refs =
                new List<Tuple<string, string, string, string>>();

            public override void Visit(NamedTableReference node)
            {
                var s = node.SchemaObject != null && node.SchemaObject.SchemaIdentifier != null
                    ? node.SchemaObject.SchemaIdentifier.Value
                    : "dbo";

                var n = node.SchemaObject != null && node.SchemaObject.BaseIdentifier != null
                    ? node.SchemaObject.BaseIdentifier.Value
                    : null;

                if (n != null)
                    Refs.Add(Tuple.Create(s, n, "TABLE_OR_VIEW", "ReadsFrom"));
            }

            public override void Visit(InsertStatement node)
            {
                var t = node.InsertSpecification != null
                    ? node.InsertSpecification.Target as NamedTableReference
                    : null;

                if (t != null && t.SchemaObject != null && t.SchemaObject.BaseIdentifier != null)
                {
                    var schema = t.SchemaObject.SchemaIdentifier != null
                        ? t.SchemaObject.SchemaIdentifier.Value
                        : "dbo";

                    Refs.Add(Tuple.Create(schema, t.SchemaObject.BaseIdentifier.Value, "TABLE", "WritesTo"));
                }
            }

            public override void Visit(UpdateStatement node)
            {
                var t = node.UpdateSpecification != null
                    ? node.UpdateSpecification.Target as NamedTableReference
                    : null;

                if (t != null && t.SchemaObject != null && t.SchemaObject.BaseIdentifier != null)
                {
                    var schema = t.SchemaObject.SchemaIdentifier != null
                        ? t.SchemaObject.SchemaIdentifier.Value
                        : "dbo";

                    Refs.Add(Tuple.Create(schema, t.SchemaObject.BaseIdentifier.Value, "TABLE", "WritesTo"));
                }
            }

            public override void Visit(DeleteStatement node)
            {
                var t = node.DeleteSpecification != null
                    ? node.DeleteSpecification.Target as NamedTableReference
                    : null;

                if (t != null && t.SchemaObject != null && t.SchemaObject.BaseIdentifier != null)
                {
                    var schema = t.SchemaObject.SchemaIdentifier != null
                        ? t.SchemaObject.SchemaIdentifier.Value
                        : "dbo";

                    Refs.Add(Tuple.Create(schema, t.SchemaObject.BaseIdentifier.Value, "TABLE", "WritesTo"));
                }
            }

            public override void Visit(ExecuteSpecification node)
            {
                var pr = node.ExecutableEntity as ExecutableProcedureReference;
                if (pr != null &&
                    pr.ProcedureReference != null &&
                    pr.ProcedureReference.ProcedureReference != null &&
                    pr.ProcedureReference.ProcedureReference.Name != null)
                {
                    var nm = pr.ProcedureReference.ProcedureReference.Name;
                    var schema = nm.SchemaIdentifier != null ? nm.SchemaIdentifier.Value : "dbo";
                    Refs.Add(Tuple.Create(schema, nm.BaseIdentifier.Value, "PROC", "Executes"));
                }
            }
        }

        // ========================================================
        //  Persist
        // ========================================================
        private static void WriteGraph(string outDir, IEnumerable<NodeRow> nodes, IEnumerable<EdgeRow> edges)
        {
            var graphDir = Path.Combine(outDir, "graph");
            Directory.CreateDirectory(graphDir);

            var nodesCsv = Path.Combine(graphDir, "nodes.csv");
            var edgesCsv = Path.Combine(graphDir, "edges.csv");
            var graphJson = Path.Combine(graphDir, "graph.json");

            File.WriteAllLines(
                nodesCsv,
                (new[] { "key,kind,name,schema,file,batch,domain,body_path" })
                    .Concat(nodes.OrderBy(n => n.Key)
                        .Select(n => Csv(n.Key, n.Kind, n.Name, n.Schema, ToRel(n.File), n.Batch, n.Domain, n.BodyPath))));

            File.WriteAllLines(
                edgesCsv,
                (new[] { "from,to,relation,to_kind,file,batch" })
                    .Concat(edges.OrderBy(e => e.From).ThenBy(e => e.To)
                        .Select(e => Csv(e.From, e.To, e.Relation, e.ToKind, ToRel(e.File), e.Batch))));

            var graph = new
            {
                nodes = nodes.OrderBy(n => n.Key).ToArray(),
                edges = edges.OrderBy(e => e.From).ThenBy(e => e.To).ToArray()
            };

            File.WriteAllText(graphJson, JsonConvert.SerializeObject(graph, Formatting.Indented));
            ConsoleLog.Info("Written graph artifacts: " + nodesCsv + " | " + edgesCsv + " | " + graphJson);
        }

        private static void WriteManifest(
            string outDir,
            string sqlRoot,
            List<string> codeRoots,
            int nodeCount,
            int edgeCount,
            int bodiesCount)
        {
            var manifestObj = new
            {
                schema = 1,
                builtAt = DateTimeOffset.Now.ToString("o"),
                sqlRoot,
                codeRoots,
                counts = new { nodes = nodeCount, edges = edgeCount, docs = bodiesCount }
            };

            var manifestPath = Path.Combine(outDir, "manifest.json");
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifestObj, Formatting.Indented));
            ConsoleLog.Info("Manifest written: " + manifestPath);
        }

        private static string Csv(params object[] vals)
            => string.Join(",", vals.Select(v =>
                "\"" + ((v != null ? v.ToString() : string.Empty).Replace("\"", "\"\"")) + "\""));

        private static string ToRel(string p) => p == null ? null : p.Replace('\\', '/');

        // ===== Utilities =====
        private static string GetRelativePathSafe(string baseDir, string fullPath)
        {
            try
            {
                var baseUri = new Uri(AppendDirSep(baseDir));
                var pathUri = new Uri(fullPath);
                var rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString());
                return rel.Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return fullPath;
            }
        }

        private static string AppendDirSep(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return dir;
            if (dir.EndsWith(Path.DirectorySeparatorChar.ToString())) return dir;
            return dir + Path.DirectorySeparatorChar;
        }
    }
}