using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Core.Sql
{
    /// <summary>
    /// Heuristic scanner for inline SQL inside C# files under paths.InlineSqlPath.
    /// It:
    ///  - walks *.cs files under configured root(s),
    ///  - extracts string literals that look "SQL-ish",
    ///  - optionally parses them with ScriptDom to find table/proc references,
    ///  - emits SqlArtifact with ArtifactKind = "InlineSQL".
    ///
    /// The goal is to produce stable identifiers that RAG can consume,
    /// without touching LegacySqlIndexer (graph builder).
    /// </summary>
    public static class InlineSqlScanner
    {
        // Very small SQL keyword set – tuned to avoid false positives on short strings.
        private static readonly string[] SqlKeywords = new[]
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE",
            "EXEC", "EXECUTE",
            "CREATE", "ALTER", "DROP",
            "FROM", "WHERE", "JOIN"
        };

        // Allow multiple roots: ";" (Windows PATH), ":" (Linux PATH via PathSeparator), or "|".
        private static readonly char[] RootSeparators = new[]
        {
            ';',
            '|',
            System.IO.Path.PathSeparator
        };

        // Simple literal matcher: "......" (at least 5 chars inside, to avoid tiny strings).
        private static readonly Regex StringLiteralRegex =
            new Regex("\"([^\"]{5,})\"", RegexOptions.Compiled);

        /// <summary>
        /// Entry point used by ScriptDomSqlModelExtractor_waiting.
        /// Accepts a single string that may contain multiple roots (separated by ;, |, or PathSeparator).
        /// </summary>
        public static IEnumerable<SqlArtifact> ScanInlineSql(string inlineSqlRoots)
        {
            if (string.IsNullOrWhiteSpace(inlineSqlRoots))
                yield break;

            foreach (var root in SplitRoots(inlineSqlRoots))
            {
                var fullRoot = GetFullPathSafe(root);
                if (string.IsNullOrWhiteSpace(fullRoot) || !Directory.Exists(fullRoot))
                    continue;

                foreach (var file in EnumerateCSharpFiles(fullRoot))
                {
                    foreach (var art in ExtractFromCSharpFile(fullRoot, file))
                        yield return art;
                }
            }
        }

        private static IEnumerable<string> SplitRoots(string raw)
        {
            var parts = raw.Split(RootSeparators, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                    yield return trimmed;
            }
        }

        private static string GetFullPathSafe(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static IEnumerable<string> EnumerateCSharpFiles(string root)
        {
            var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git",
                ".vs",
                "bin",
                "obj",
                "node_modules",
                "packages"
            };

            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                string[] subdirs;
                try
                {
                    subdirs = Directory.GetDirectories(dir);
                }
                catch
                {
                    continue;
                }

                foreach (var d in subdirs)
                {
                    var name = Path.GetFileName(d);
                    if (ignore.Contains(name))
                        continue;

                    stack.Push(d);
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(dir, "*.cs");
                }
                catch
                {
                    continue;
                }

                foreach (var f in files)
                    yield return f;
            }
        }

        private static IEnumerable<SqlArtifact> ExtractFromCSharpFile(string root, string filePath)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath);
            }
            catch
            {
                yield break;
            }

            var relPath = MakeRelativePath(root, filePath);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line == null)
                    continue;

                if (line.IndexOf('"') < 0)
                    continue; // no literals on this line

                foreach (var literal in ExtractStringLiteralsFromLine(line))
                {
                    if (!LooksLikeSql(literal))
                        continue;

                    // Try to get referenced tables/procs; if none, still emit a generic InlineSQL artifact.
                    bool any = false;
                    foreach (var art in AnalyzeSqlSnippet(filePath, relPath, i + 1, literal))
                    {
                        any = true;
                        yield return art;
                    }

                    if (!any)
                    {
                        var id = string.Format(
                            "inline:{0}:L{1}",
                            relPath,
                            i + 1);

                        yield return new SqlArtifact(
                            sourcePath: filePath,
                            artifactKind: "InlineSQL",
                            identifier: id);
                    }
                }
            }
        }

        private static IEnumerable<string> ExtractStringLiteralsFromLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                yield break;

            var matches = StringLiteralRegex.Matches(line);
            foreach (Match m in matches)
            {
                if (!m.Success)
                    continue;

                var val = m.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(val))
                    yield return val;
            }
        }

        private static bool LooksLikeSql(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // Extremely short strings are almost never inline SQL.
            if (value.Length < 12)
                return false;

            var text = value.ToUpperInvariant();

            int hits = 0;
            for (int i = 0; i < SqlKeywords.Length; i++)
            {
                if (text.IndexOf(SqlKeywords[i], StringComparison.Ordinal) >= 0)
                {
                    hits++;
                    if (hits >= 2)
                        return true;
                }
            }

            // One keyword + typical SQL constructs can still be good enough.
            if (hits == 1 &&
                (text.Contains(" FROM ")
                 || text.Contains(" INTO ")
                 || text.Contains(" TABLE ")))
            {
                return true;
            }

            return false;
        }

        private static IEnumerable<SqlArtifact> AnalyzeSqlSnippet(
    string filePath,
    string relPath,
    int lineNumber,
    string sql)
        {
            var result = new List<SqlArtifact>();

            var parser = new TSql150Parser(initialQuotedIdentifiers: true);
            try
            {
                IList<ParseError> errors;
                TSqlFragment fragment;
                using (var reader = new StringReader(sql))
                {
                    fragment = parser.Parse(reader, out errors);
                }

                if (fragment == null)
                    return result;

                var collector = new MiniSqlRefCollector();
                fragment.Accept(collector);

                foreach (var r in collector.Refs)
                {
                    var schema = string.IsNullOrEmpty(r.Schema) ? "dbo" : r.Schema;
                    var name = string.IsNullOrEmpty(r.Name) ? "(anon)" : r.Name;

                    // Identifier encodes both SQL object and origin in C#:
                    //   dbo.Customer|TABLE_OR_VIEW|inline@Some/File.cs:L42
                    var id = string.Format(
                        "{0}.{1}|{2}|inline@{3}:L{4}",
                        schema,
                        name,
                        r.Kind,
                        relPath,
                        lineNumber);

                    result.Add(new SqlArtifact(
                        sourcePath: filePath,
                        artifactKind: "InlineSQL",
                        identifier: id));
                }
            }
            catch
            {
                // On parse failure we just return whatever we collected so far (likely empty).
            }

            return result;
        }

        /// <summary>
        /// Minimal collector for table/proc references inside a SQL fragment.
        /// This is intentionally small and duplicated from LegacySqlIndexer logic
        /// to avoid cross-coupling Core pipeline with the graph builder.
        /// </summary>
        private sealed class MiniSqlRefCollector : TSqlFragmentVisitor
        {
            internal sealed class RefInfo
            {
                public string Schema { get; private set; }
                public string Name { get; private set; }
                public string Kind { get; private set; }

                public RefInfo(string schema, string name, string kind)
                {
                    Schema = schema;
                    Name = name;
                    Kind = kind;
                }
            }

            private readonly List<RefInfo> _refs = new List<RefInfo>();

            public IReadOnlyList<RefInfo> Refs
            {
                get { return _refs; }
            }

            public override void ExplicitVisit(ExecuteSpecification node)
            {
                if (node == null || node.ExecutableEntity == null)
                    return;

                var exec = node.ExecutableEntity as ExecutableProcedureReference;
                if (exec == null)
                    return;

                // ExecutableProcedureReference.ProcedureReference : ProcedureReferenceName
                var procRefName = exec.ProcedureReference;
                if (procRefName == null || procRefName.ProcedureReference == null)
                    return;

                // ProcedureReferenceName.ProcedureReference : ProcedureReference
                var procRef = procRefName.ProcedureReference;
                if (procRef.Name == null || procRef.Name.BaseIdentifier == null)
                    return;

                var schemaObject = procRef.Name;

                var schema = schemaObject.SchemaIdentifier != null
                    ? schemaObject.SchemaIdentifier.Value
                    : "dbo";

                var name = schemaObject.BaseIdentifier.Value;

                _refs.Add(new RefInfo(schema, name, "PROC"));
            }
        }

        private static string MakeRelativePath(string baseDir, string fullPath)
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