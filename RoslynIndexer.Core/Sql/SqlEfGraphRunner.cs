using RoslynIndexer.Core.Logging;
using RoslynIndexer.Core.Solutions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RoslynIndexer.Core.Sql
{
    /// <summary>
    /// Shared helper used by both Net9 and Net48 runners to drive the legacy SQL/EF graph.
    /// Supports three modes:
    /// - SQL + EF root (classic),
    /// - SQL only,
    /// - EF-only (no SQL path, EF root only).
    /// Produces sql_bodies.jsonl (and graph CSV/JSON) expected by the Python/Vector pipeline.
    /// </summary>
    public static class SqlEfGraphRunner
    {
        /// <summary>
        /// Runs the legacy SQL/EF graph and ensures sql_bodies.jsonl is produced.
        /// SQL/EF artifacts are written under: &lt;tempRoot&gt;\sql_code_bundle\*.
        /// Additionally, TABLE nodes are added to the graph so it is closed (nodes + edges).
        /// </summary>
        /// <summary>
        /// Runs the legacy SQL/EF graph and ensures sql_bodies.jsonl is produced.
        /// SQL/EF artifacts are written under: <tempRoot>\sql_code_bundle\*.
        /// Additionally, TABLE nodes are added to the graph so it is closed (nodes + edges).
        /// </summary>
        public static void Run(string tempRoot, string sqlPath, string efPath, string solutionPath)
        {
            // -------------------------------------------
            // 0) Resolve projects from solution (.sln/.slnx)
            // -------------------------------------------
            SolutionFileInfo? solutionInfo = null;
            string[] projectsFromSolution = Array.Empty<string>();

            if (!string.IsNullOrWhiteSpace(solutionPath))
            {
                try
                {
                    solutionInfo = SolutionFileLoader.Load(solutionPath);

                    if (solutionInfo.ProjectPaths is { Count: > 0 })
                    {
                        projectsFromSolution = solutionInfo.ProjectPaths.ToArray();
                        ConsoleLog.Info("[SQL/EF] Solution projects discovered: " + projectsFromSolution.Length);
                    }
                    else
                    {
                        ConsoleLog.Warn("[SQL/EF] Solution file loaded but no supported projects were found.");
                    }
                }
                catch (Exception ex)
                {
                    // We do not want to fail the whole SQL/EF run if solution parsing fails.
                    ConsoleLog.Warn("[SQL/EF] Failed to load solution file '" + solutionPath + "': " + ex.Message);
                }
            }
            else
            {
                ConsoleLog.Warn("[SQL/EF] No solution path provided for SQL/EF graph run.");
            }

            // NOTE:
            // For now 'projectsFromSolution' is only resolved and logged.
            // SqlEfGraphIndexer still uses sqlPath/efPath as before.
            // In future we can use 'projectsFromSolution' to restrict EF/migrations/inline SQL scanning.

            // -------------------------------------------
            // 1) Validate SQL/EF roots (existing behavior)
            // -------------------------------------------
            var hasSql = !string.IsNullOrWhiteSpace(sqlPath);
            var hasEf = !string.IsNullOrWhiteSpace(efPath) && Directory.Exists(efPath);

            if (!hasSql && !hasEf)
            {
                ConsoleLog.Info("[SQL/EF] Skipped (no SQL or EF path).");
                return;
            }

            string sqlRootForIndexer;

            if (hasSql)
            {
                if (!Directory.Exists(sqlPath))
                    throw new DirectoryNotFoundException("[SQL] Path not found: " + sqlPath);

                sqlRootForIndexer = sqlPath;

                ConsoleLog.Info(hasEf
                    ? "[SQL/EF] Using SQL root: " + sqlPath + " and EF root: " + efPath
                    : "[SQL/EF] Using SQL root: " + sqlPath + " (no EF root).");
            }
            else
            {
                // EF-only mode: use EF root as sqlProjectRoot so EF-only projects still produce a graph.
                if (!hasEf)
                    throw new DirectoryNotFoundException("[EF] EF root not found, cannot run EF-only mode.");

                sqlRootForIndexer = efPath;
                ConsoleLog.Warn("[SQL/EF] No SQL root provided; running EF-only graph from: " + efPath);
            }

            // -------------------------------------------
            // 2) Run legacy SQL/EF indexer
            // -------------------------------------------
            // All legacy SQL/EF artifacts go under this folder inside tempRoot.
            var sqlBundleRoot = Path.Combine(tempRoot, "sql_code_bundle");
            Directory.CreateDirectory(sqlBundleRoot);

            // Run the legacy indexer (writes docs/graph/manifest.json under sql_code_bundle).
            SqlEfGraphIndexer.Start(
                outputDir: sqlBundleRoot,
                sqlProjectRoot: sqlRootForIndexer,
                efRoot: hasEf ? efPath : null
            );

            // Verify artifact expected by Python/Vector pipeline.
            var sqlBodies = Directory
                .EnumerateFiles(sqlBundleRoot, "sql_bodies.jsonl", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (sqlBodies == null)
            {
                throw new InvalidOperationException(
                    "[SQL] Expected 'sql_bodies.jsonl' not produced under " + sqlBundleRoot +
                    ". Verify 'paths.sql' or 'paths.ef' point to a valid project root.");
            }

            ConsoleLog.Info("[SQL] Produced: " + sqlBodies);

            // -------------------------------------------
            // 3) Enrich graph with TABLE nodes
            // -------------------------------------------
            try
            {
                EnrichGraphWithTableNodes(sqlBundleRoot);
            }
            catch (Exception ex)
            {
                ConsoleLog.Warn("[SQL] WARNING: failed to enrich SQL graph with TABLE nodes: " + ex.Message);
            }
        }
        /// <summary>
        /// Adds missing TABLE nodes based on edges.csv and regenerates graph.json from CSV files.
        /// </summary>
        private static void EnrichGraphWithTableNodes(string sqlBundleRoot)
        {
            var graphDir = Path.Combine(sqlBundleRoot, "graph");
            var nodesPath = Path.Combine(graphDir, "nodes.csv");
            var edgesPath = Path.Combine(graphDir, "edges.csv");
            var graphJsonPath = Path.Combine(graphDir, "graph.json");

            if (!File.Exists(nodesPath) || !File.Exists(edgesPath))
            {
                ConsoleLog.Warn("[SQL] Graph CSV files not found – skipping TABLE-node enrichment.");
                return;
            }

            var nodeLines = File.ReadAllLines(nodesPath).ToList();
            if (nodeLines.Count == 0)
                return;

            // Collect existing node keys.
            var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < nodeLines.Count; i++)
            {
                var line = nodeLines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var cols = SplitCsv(line);
                if (cols.Length == 0)
                    continue;

                var key = cols[0];
                if (!string.IsNullOrWhiteSpace(key))
                    existingKeys.Add(key);
            }

            // Scan edges for TABLE targets.
            var tableNodesToAdd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // key -> file

            var edgesLines = File.ReadAllLines(edgesPath);
            for (int i = 1; i < edgesLines.Length; i++)
            {
                var line = edgesLines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var cols = SplitCsv(line);
                if (cols.Length < 6)
                    continue;

                var toKey = cols[1];     // to
                var toKind = cols[3];    // to_kind
                var file = cols[4];      // file

                if (!string.Equals(toKind, "TABLE", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(toKey))
                    continue;

                if (existingKeys.Contains(toKey))
                    continue;

                if (!tableNodesToAdd.ContainsKey(toKey))
                    tableNodesToAdd[toKey] = file ?? string.Empty;
            }

            if (tableNodesToAdd.Count == 0)
            {
                ConsoleLog.Warn("[SQL] No missing TABLE nodes detected.");
            }
            else
            {
                ConsoleLog.Info("[SQL] Adding " + tableNodesToAdd.Count + " TABLE node(s) to nodes.csv.");

                foreach (var kvp in tableNodesToAdd)
                {
                    var key = kvp.Key;
                    var file = kvp.Value ?? string.Empty;

                    ParseTableKey(key, out var schema, out var name);

                    // nodes.csv header:
                    // key,kind,name,schema,file,batch,domain,body_path
                    var nodeLine = ToCsvRow(
                        key,                 // key
                        "TABLE",             // kind
                        name,                // name
                        schema,              // schema
                        file,                // file
                        string.Empty,        // batch
                        "db",                // domain (database objects)
                        string.Empty         // body_path
                    );

                    nodeLines.Add(nodeLine);
                    existingKeys.Add(key);
                }

                File.WriteAllLines(nodesPath, nodeLines.ToArray(), Encoding.UTF8);
            }

            // Regenerate graph.json from the updated CSV data so JSON stays in sync.
            RegenerateGraphJson(nodesPath, edgesPath, graphJsonPath);
        }

        /// <summary>
        /// Parses a table key like "dbo.ActivityLog|TABLE" into schema ("dbo") and name ("ActivityLog").
        /// For more complex names (e.g. NameCompatibilityManager.GetTableName(...)), everything after the
        /// first '.' is treated as the logical name.
        /// </summary>
        private static void ParseTableKey(string key, out string schema, out string name)
        {
            schema = string.Empty;
            name = key ?? string.Empty;

            if (string.IsNullOrWhiteSpace(key))
                return;

            var pipeIdx = key.IndexOf('|');
            var left = pipeIdx >= 0 ? key.Substring(0, pipeIdx) : key;

            var dotIdx = left.IndexOf('.');
            if (dotIdx > 0)
            {
                schema = left.Substring(0, dotIdx);
                name = left.Substring(dotIdx + 1);
            }
            else
            {
                schema = string.Empty;
                name = left;
            }
        }

        /// <summary>
        /// Very small CSV parser that respects quotes and escaped quotes ("").
        ///</summary>
        private static string[] SplitCsv(string line)
        {
            var result = new List<string>();
            if (line == null)
                return Array.Empty<string>();

            var sb = new StringBuilder();
            var inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (c == '"')
                {
                    // Escaped quote ("")
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++; // skip second quote
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }

            result.Add(sb.ToString());
            return result.ToArray();
        }

        /// <summary>
        /// Builds a CSV row with proper quoting.
        /// </summary>
        private static string ToCsvRow(params string[] columns)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < columns.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');

                var value = columns[i] ?? string.Empty;
                // Always quote, double-escape quotes.
                sb.Append('"');
                sb.Append(value.Replace("\"", "\"\""));
                sb.Append('"');
            }

            return sb.ToString();
        }

        /// <summary>
        /// Regenerates graph.json from nodes.csv and edges.csv.
        /// We keep JSON structure: { "nodes": [...], "edges": [...] }.
        /// </summary>
        private static void RegenerateGraphJson(string nodesPath, string edgesPath, string graphJsonPath)
        {
            var nodes = new List<GraphNode>();
            var edges = new List<GraphEdge>();

            var nodeLines = File.ReadAllLines(nodesPath);
            for (int i = 1; i < nodeLines.Length; i++)
            {
                var line = nodeLines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var cols = SplitCsv(line);
                if (cols.Length < 8)
                    continue;

                nodes.Add(new GraphNode
                {
                    Key = cols[0],
                    Kind = cols[1],
                    Name = cols[2],
                    Schema = cols[3],
                    File = cols[4],
                    Batch = cols[5],
                    Domain = cols[6],
                    BodyPath = cols[7]
                });
            }

            var edgeLines = File.ReadAllLines(edgesPath);
            for (int i = 1; i < edgeLines.Length; i++)
            {
                var line = edgeLines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var cols = SplitCsv(line);
                if (cols.Length < 6)
                    continue;

                edges.Add(new GraphEdge
                {
                    From = cols[0],
                    To = cols[1],
                    Relation = cols[2],
                    ToKind = cols[3],
                    File = cols[4],
                    Batch = cols[5]
                });
            }

            using (var writer = new StreamWriter(graphJsonPath, false, Encoding.UTF8))
            {
                writer.Write("{\"nodes\":[");

                for (int i = 0; i < nodes.Count; i++)
                {
                    if (i > 0)
                        writer.Write(',');

                    var n = nodes[i];
                    writer.Write('{');
                    WriteJsonProp(writer, "Key", n.Key, true);
                    WriteJsonProp(writer, "Kind", n.Kind, true);
                    WriteJsonProp(writer, "Name", n.Name, true);
                    WriteJsonProp(writer, "Schema", n.Schema, true);
                    WriteJsonProp(writer, "File", n.File, true);
                    WriteJsonProp(writer, "Batch", n.Batch, true);
                    WriteJsonProp(writer, "Domain", n.Domain, true);
                    WriteJsonProp(writer, "BodyPath", n.BodyPath, false);
                    writer.Write('}');
                }

                writer.Write("],\"edges\":[");

                for (int i = 0; i < edges.Count; i++)
                {
                    if (i > 0)
                        writer.Write(',');

                    var e = edges[i];
                    writer.Write('{');
                    WriteJsonProp(writer, "From", e.From, true);
                    WriteJsonProp(writer, "To", e.To, true);
                    WriteJsonProp(writer, "Relation", e.Relation, true);
                    WriteJsonProp(writer, "ToKind", e.ToKind, true);
                    WriteJsonProp(writer, "File", e.File, true);
                    WriteJsonProp(writer, "Batch", e.Batch, false);
                    writer.Write('}');
                }

                writer.Write("]}");
            }

            ConsoleLog.Info($"[SQL] Regenerated graph.json from CSV (nodes={nodes.Count}, edges={edges.Count}).")                ;
        }

        private static void WriteJsonProp(StreamWriter writer, string name, string value, bool appendComma)
        {
            writer.Write('\"');
            writer.Write(name);
            writer.Write("\":");
            writer.Write(ToJsonString(value ?? string.Empty));
            if (appendComma)
                writer.Write(',');
        }

        private static string ToJsonString(string value)
        {
            if (value == null)
                return "\"\"";

            var sb = new StringBuilder();
            sb.Append('\"');

            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\"':
                        sb.Append("\\\"");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < ' ')
                        {
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(ch);
                        }
                        break;
                }
            }

            sb.Append('\"');
            return sb.ToString();
        }

        // ----------------- small DTOs for JSON regen -----------------

        private sealed class GraphNode
        {
            public string Key { get; set; }
            public string Kind { get; set; }
            public string Name { get; set; }
            public string Schema { get; set; }
            public string File { get; set; }
            public string Batch { get; set; }
            public string Domain { get; set; }
            public string BodyPath { get; set; }
        }

        private sealed class GraphEdge
        {
            public string From { get; set; }
            public string To { get; set; }
            public string Relation { get; set; }
            public string ToKind { get; set; }
            public string File { get; set; }
            public string Batch { get; set; }
        }
    }
}
