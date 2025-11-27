using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

        // Built-in hot methods for inline SQL wrappers (EF / nopCommerce-style).
        // These are always considered SQL entry points; the first string argument
        // is analyzed as SQL without using LooksLikeSql.
        private static readonly string[] BuiltInHotMethods = new[]
        {
            "SqlQuery",   // EF6 / EF Core / nopCommerce
            "ExecuteSql", // ExecuteSqlCommand / ExecuteSqlRaw / ExecuteSqlInterpolated*
            "FromSql"     // FromSql / FromSqlRaw / FromSqlInterpolated*
        };

        // Allow multiple roots: ";" (Windows PATH), ":" (Linux PATH via PathSeparator), or "|".
        private static readonly char[] RootSeparators = new[]
        {
            ';',
            '|',
            System.IO.Path.PathSeparator
        };

        // Ignore typical build / artifacts / packages folders.
        private static readonly HashSet<string> DefaultIgnoreDirs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git",
                ".svn",
                ".hg",
                "bin",
                "obj",
                "packages"
            };

        // Regex for very basic C# string literals on a single line: "...."
        // We intentionally avoid full C# parsing here to keep the scanner cheap.
        private static readonly Regex StringLiteralRegex =
            new Regex("\"([^\"]*)\"", RegexOptions.Compiled);

        /// <summary>
        /// Scans one or more root directories (separated with ';', '|', or Path.PathSeparator)
        /// for C# files and returns all detected inline SQL occurrences as SqlArtifact.
        ///
        /// This overload uses built-in hot methods only (SqlQuery / ExecuteSql / FromSql).
        /// </summary>
        public static IEnumerable<SqlArtifact> ScanInlineSql(string roots)
        {
            foreach (var artifact in ScanInlineSql(roots, extraHotMethods: null))
            {
                yield return artifact;
            }
        }

        /// <summary>
        /// Scans one or more root directories (separated with ';', '|', or Path.PathSeparator)
        /// for C# files and returns all detected inline SQL occurrences as SqlArtifact.
        ///
        /// extraHotMethods can be used to extend the built-in hot methods list with
        /// project-specific wrappers (e.g., Dapper Query/Execute).
        /// </summary>
        public static IEnumerable<SqlArtifact> ScanInlineSql(string roots, IEnumerable<string>? extraHotMethods)
        {
            if (string.IsNullOrWhiteSpace(roots))
                yield break;

            var hotMethods = BuildEffectiveHotMethods(extraHotMethods);

            foreach (var root in SplitRoots(roots))
            {
                foreach (var file in EnumerateCSharpFiles(root, DefaultIgnoreDirs))
                {
                    foreach (var artifact in ExtractFromCSharpFile(root, file, hotMethods))
                    {
                        yield return artifact;
                    }
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

        private static IEnumerable<string> EnumerateCSharpFiles(string root, HashSet<string> ignore)
        {
            if (string.IsNullOrWhiteSpace(root))
                yield break;

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch
            {
                yield break;
            }

            if (!Directory.Exists(fullRoot))
                yield break;

            var stack = new Stack<string>();
            stack.Push(fullRoot);

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

        /// <summary>
        /// Builds the effective hot methods list: built-in EF-style wrappers
        /// plus any extra methods provided from configuration.
        /// </summary>
        private static string[] BuildEffectiveHotMethods(IEnumerable<string>? extraHotMethods)
        {
            var set = new HashSet<string>(BuiltInHotMethods, StringComparer.Ordinal);

            if (extraHotMethods == null)
                return set.ToArray();

            foreach (var method in extraHotMethods)
            {
                if (string.IsNullOrWhiteSpace(method))
                    continue;

                var trimmed = method.Trim();
                if (trimmed.Length == 0)
                    continue;

                set.Add(trimmed);
            }

            return set.ToArray();
        }

        /// <summary>
        /// Checks whether the invocation text contains any of the configured hot method tokens.
        /// </summary>
        private static bool ContainsAnyHotMethod(string callText, string[] hotMethods)
        {
            if (string.IsNullOrEmpty(callText))
                return false;

            if (hotMethods == null || hotMethods.Length == 0)
                return false;

            for (int i = 0; i < hotMethods.Length; i++)
            {
                var token = hotMethods[i];
                if (string.IsNullOrEmpty(token))
                    continue;

                if (callText.IndexOf(token, StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }

        // RoslynIndexer.Core/Sql/InlineSqlScanner.cs

        private static IEnumerable<SqlArtifact> ExtractFromCSharpFile(string root, string filePath, string[] hotMethods)
        {
            string text;
            try
            {
                text = File.ReadAllText(filePath);
            }
            catch
            {
                // If we cannot read the file, we cannot scan it.
                yield break;
            }

            var relPath = MakeRelativePath(root, filePath);

            SyntaxTree? tree = null;
            SyntaxNode? rootNode = null;
            MethodContext[]? methodContexts = null;

            try
            {
                // Parse C# file with Roslyn so we can get precise string literals,
                // including multi-line verbatim strings (@"...").
                tree = CSharpSyntaxTree.ParseText(text, path: filePath);
                rootNode = tree.GetRoot();
                methodContexts = BuildMethodContexts(rootNode, tree);
            }
            catch
            {
                // If parsing fails for any reason, we fall back to null contexts
                // and will not enrich with C# method info.
                rootNode = null;
                methodContexts = null;
            }

            // Preferred path: use Roslyn string literals so we handle multi-line strings.
            if (rootNode != null)
            {
                // Lines that were already handled via explicit hot methods
                // (SqlQuery / ExecuteSql / FromSql and configured extras).
                var specialLines = new HashSet<int>();

                // 1) Hot methods path: calls like SqlQuery / ExecuteSql / FromSql / extraHotMethods
                var invocations = rootNode.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var inv in invocations)
                {
                    var callText = inv.Expression.ToString();
                    if (string.IsNullOrEmpty(callText))
                        continue;

                    if (!ContainsAnyHotMethod(callText, hotMethods))
                    {
                        continue;
                    }

                    var argList = inv.ArgumentList;
                    if (argList == null)
                        continue;

                    var args = argList.Arguments;
                    if (args.Count == 0)
                        continue;

                    var firstArgExpr = args[0].Expression;

                    // Try to resolve SQL text from:
                    //  - inline string literal,
                    //  - or simple variable/const initialized with a string literal.
                    var sqlText = TryGetSqlTextFromArgument(firstArgExpr, rootNode);
                    if (string.IsNullOrWhiteSpace(sqlText))
                    {
                        // In the future we could log a warning here that argument is non-literal
                        // and could not be resolved to a constant string.
                        continue;
                    }

                    // We use the argument location (not the initializer) as the "inline" location.
                    var span = firstArgExpr.GetLocation().GetLineSpan();
                    var lineNumber = span.StartLinePosition.Line + 1;

                    MethodContext? ctx = null;
                    if (methodContexts != null)
                    {
                        ctx = FindMethodContextForLine(methodContexts, lineNumber);
                    }

                    bool any = false;

                    // IMPORTANT: for hot methods we do NOT require LooksLikeSql,
                    // we always try to analyze the snippet.
                    foreach (var art in AnalyzeSqlSnippet(filePath, relPath, lineNumber, sqlText))
                    {
                        any = true;
                        EnrichArtifactWithContext(art, relPath, lineNumber, ctx);
                        yield return art;
                    }

                    if (!any)
                    {
                        // Even if parsing produced no table/proc refs, we still emit a generic InlineSQL artifact.
                        var id = string.Format("inline:{0}:L{1}", relPath, lineNumber);

                        var artifact = new SqlArtifact(
                            sourcePath: filePath,
                            artifactKind: "InlineSQL",
                            identifier: id);

                        EnrichArtifactWithContext(artifact, relPath, lineNumber, ctx);
                        yield return artifact;
                    }

                    // Mark this line as already processed so we do not double-handle it below.
                    specialLines.Add(lineNumber);
                }

                // 2) Generic path: all string literals that "look like SQL"
                var stringLiterals = rootNode
                    .DescendantNodes()
                    .OfType<LiteralExpressionSyntax>()
                    .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression));

                foreach (var literalExpr in stringLiterals)
                {
                    var valueText = literalExpr.Token.ValueText;
                    if (string.IsNullOrWhiteSpace(valueText))
                        continue;

                    var span = literalExpr.GetLocation().GetLineSpan();
                    var lineNumber = span.StartLinePosition.Line + 1;

                    // Skip literals that were already handled in the hot methods branch
                    if (specialLines.Contains(lineNumber))
                        continue;

                    if (!LooksLikeSql(valueText))
                        continue;

                    MethodContext? ctx = null;
                    if (methodContexts != null)
                    {
                        ctx = FindMethodContextForLine(methodContexts, lineNumber);
                    }

                    bool any = false;

                    foreach (var art in AnalyzeSqlSnippet(filePath, relPath, lineNumber, valueText))
                    {
                        any = true;
                        EnrichArtifactWithContext(art, relPath, lineNumber, ctx);
                        yield return art;
                    }

                    if (!any)
                    {
                        // Even if parsing produced no table/proc refs, we still emit a generic InlineSQL artifact.
                        var id = string.Format("inline:{0}:L{1}", relPath, lineNumber);

                        var artifact = new SqlArtifact(
                            sourcePath: filePath,
                            artifactKind: "InlineSQL",
                            identifier: id);

                        EnrichArtifactWithContext(artifact, relPath, lineNumber, ctx);
                        yield return artifact;
                    }
                }

                // Roslyn path handled everything, we are done.
                yield break;
            }

            // Fallback: if for some reason we could not get a syntax root,
            // use the old line-based logic as a best effort.
            string[] lines;
            try
            {
                lines = text
                    .Replace("\r\n", "\n")
                    .Replace("\r", "\n")
                    .Split(new[] { "\n" }, StringSplitOptions.None);
            }
            catch
            {
                yield break;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.IndexOf('"') < 0)
                    continue;

                MethodContext? ctx = null;
                if (methodContexts != null)
                {
                    ctx = FindMethodContextForLine(methodContexts, i + 1);
                }

                foreach (var literal in ExtractStringLiteralsFromLine(line))
                {
                    if (!LooksLikeSql(literal))
                        continue;

                    bool any = false;

                    foreach (var art in AnalyzeSqlSnippet(filePath, relPath, i + 1, literal))
                    {
                        any = true;
                        EnrichArtifactWithContext(art, relPath, i + 1, ctx);
                        yield return art;
                    }

                    if (!any)
                    {
                        var id = string.Format("inline:{0}:L{1}", relPath, i + 1);

                        var artifact = new SqlArtifact(
                            sourcePath: filePath,
                            artifactKind: "InlineSQL",
                            identifier: id);

                        EnrichArtifactWithContext(artifact, relPath, i + 1, ctx);
                        yield return artifact;
                    }
                }
            }
        }

        /// <summary>
        /// Tries to resolve SQL text from an argument expression:
        ///  - direct string literal,
        ///  - or simple variable/const initialized with a string literal.
        /// This is a lightweight heuristic; it does not perform full data-flow analysis.
        /// </summary>
        private static string? TryGetSqlTextFromArgument(ExpressionSyntax expr, SyntaxNode rootNode)
        {
            // Case 1: direct string literal in the call, e.g. SqlQuery("SELECT ...")
            if (expr is LiteralExpressionSyntax lit &&
                lit.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return lit.Token.ValueText;
            }

            // Case 2: identifier referring to a local/const/field initialized with a string literal.
            // Example:
            //   const string sql = "SELECT ...";
            //   FakeDb.SqlQuery(sql);
            //
            // or:
            //   var sql = "SELECT ...";
            //   FakeDb.SqlQuery(sql);
            if (expr is IdentifierNameSyntax id)
            {
                var name = id.Identifier.ValueText;
                if (string.IsNullOrEmpty(name))
                    return null;

                // We restrict the search scope to the containing method if possible,
                // otherwise we fall back to the whole syntax tree root.
                SyntaxNode? scope = expr;
                while (scope != null && scope is not MethodDeclarationSyntax)
                {
                    scope = scope.Parent;
                }

                if (scope == null)
                {
                    scope = rootNode;
                }

                // Look for the first variable declarator with matching name and string literal initializer.
                var declarator = scope
                    .DescendantNodes()
                    .OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(d =>
                        string.Equals(d.Identifier.ValueText, name, StringComparison.Ordinal) &&
                        d.Initializer != null &&
                        d.Initializer.Value is LiteralExpressionSyntax);

                if (declarator?.Initializer?.Value is LiteralExpressionSyntax initLit &&
                    initLit.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    return initLit.Token.ValueText;
                }
            }

            // We do not attempt to resolve complex expressions, interpolated strings,
            // or variables whose value is computed at runtime.
            return null;
        }



        private static IEnumerable<string> ExtractStringLiteralsFromLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
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

            // Zamieniamy wszystkie białe znaki na pojedyncze spacje
            var v = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").ToUpperInvariant();

            if (v.Length < 12)
                return false;

            bool hasVerb = v.Contains("SELECT") || v.Contains("INSERT") ||
                           v.Contains("UPDATE") || v.Contains("DELETE") ||
                           v.Contains("MERGE") || v.Contains("EXEC");

            if (!hasVerb)
                return false;

            bool hasStructure = v.Contains("FROM ") || v.Contains(" INTO ") ||
                                v.Contains(" WHERE ") || v.Contains(" JOIN ") ||
                                v.Contains(" TABLE ");

            return hasStructure;
        }

        private static IEnumerable<SqlArtifact> AnalyzeSqlSnippet(
            string filePath,
            string relPath,
            int lineNumber,
            string sql)
        {
            var artifacts = new List<SqlArtifact>();

            if (string.IsNullOrWhiteSpace(sql))
                return artifacts;

            // 1) Najpierw próbujemy pełnym parserem T-SQL (ScriptDom).
            try
            {
                var parser = new TSql150Parser(initialQuotedIdentifiers: true);

                using (var reader = new StringReader(sql))
                {
                    IList<ParseError> errors;
                    var fragment = parser.Parse(reader, out errors);

                    if (fragment != null)
                    {
                        var collector = new MiniSqlRefCollector();
                        fragment.Accept(collector);

                        foreach (var r in collector.Refs)
                        {
                            var schema = string.IsNullOrEmpty(r.Schema) ? "dbo" : r.Schema;
                            var name = string.IsNullOrEmpty(r.Name) ? "(anon)" : r.Name;
                            var kind = string.IsNullOrEmpty(r.Kind) ? "TABLE_OR_VIEW" : r.Kind;

                            // Kontrakt zgodny z TryParseInlineSqlIdentifier:
                            //   {schema}.{name}|{kind}|inline@{relPath}:L{line}
                            var id = string.Format(
                                "{0}.{1}|{2}|inline@{3}:L{4}",
                                schema,
                                name,
                                kind,
                                relPath,
                                lineNumber);

                            artifacts.Add(new SqlArtifact(
                                sourcePath: filePath,
                                artifactKind: "InlineSQL",
                                identifier: id));
                        }
                    }
                }
            }
            catch
            {
                // Ignorujemy błąd parsera – przejdziemy do fallbacku regexowego.
            }

            // Jeżeli ScriptDom coś znalazł – wykorzystujemy to i kończymy.
            if (artifacts.Count > 0)
                return artifacts;

            // 2) Fallback: heurystyczny regex (to, co wcześniej już przechodziło testy).
            // Szukamy FROM / JOIN / INTO / UPDATE / MERGE / DELETE FROM z "schema.table".
            var pattern = @"\b(?:FROM|JOIN|INTO|UPDATE|MERGE|DELETE\s+FROM)\s+((?:\[[^\]]+\]|\w+)\.)?(\[[^\]]+\]|\w+)";
            var matches = Regex.Matches(sql, pattern, RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                if (!match.Success)
                    continue;

                var schemaGroup = match.Groups[1].Value;
                var nameGroup = match.Groups[2].Value;

                var schema = "dbo";

                if (!string.IsNullOrWhiteSpace(schemaGroup))
                {
                    var s = schemaGroup.Trim();

                    // "dbo." -> "dbo"
                    if (s.EndsWith("."))
                        s = s.Substring(0, s.Length - 1);

                    // "[dbo]" -> "dbo"
                    s = s.Trim('[', ']');

                    if (!string.IsNullOrWhiteSpace(s))
                        schema = s;
                }

                var name = nameGroup.Trim('[', ']');
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var identifier = string.Format(
                    "{0}.{1}|{2}|inline@{3}:L{4}",
                    schema,
                    name,
                    "TABLE_OR_VIEW",
                    relPath,
                    lineNumber);

                artifacts.Add(new SqlArtifact(
                    sourcePath: filePath,
                    artifactKind: "InlineSQL",
                    identifier: identifier));
            }

            return artifacts;
        }

        private sealed class MethodContext
        {
            public int StartLine { get; }
            public int EndLine { get; }
            public string? Namespace { get; }
            public string? TypeFullName { get; }
            public string? MethodFullName { get; }

            public MethodContext(int startLine, int endLine, string? ns, string? typeFullName, string? methodFullName)
            {
                StartLine = startLine;
                EndLine = endLine;
                Namespace = ns;
                TypeFullName = typeFullName;
                MethodFullName = methodFullName;
            }
        }

        private static MethodContext[] BuildMethodContexts(SyntaxNode rootNode, SyntaxTree tree)
        {
            var methods = rootNode.DescendantNodes()
                                  .OfType<MethodDeclarationSyntax>()
                                  .ToList();

            var result = new List<MethodContext>(methods.Count);

            foreach (var method in methods)
            {
                var span = method.GetLocation().GetLineSpan();
                var startLine = span.StartLinePosition.Line + 1;
                var endLine = span.EndLinePosition.Line + 1;

                var ns = GetNamespace(method);
                var typeName = GetContainingTypeName(method);
                var typeFullName = string.IsNullOrEmpty(typeName)
                    ? (string.IsNullOrEmpty(ns) ? null : ns)
                    : (string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName);

                var methodName = method.Identifier.ValueText;
                var methodFullName = string.IsNullOrEmpty(methodName)
                    ? typeFullName
                    : (string.IsNullOrEmpty(typeFullName) ? methodName : typeFullName + "." + methodName);

                result.Add(new MethodContext(startLine, endLine, ns, typeFullName, methodFullName));
            }

            return result.ToArray();
        }

        private static MethodContext? FindMethodContextForLine(MethodContext[] contexts, int lineNumber)
        {
            if (contexts == null || contexts.Length == 0)
                return null;

            for (int i = 0; i < contexts.Length; i++)
            {
                var ctx = contexts[i];
                if (lineNumber >= ctx.StartLine && lineNumber <= ctx.EndLine)
                    return ctx;
            }

            return null;
        }

        private static string? GetNamespace(SyntaxNode node)
        {
            var current = node.Parent;
            while (current != null)
            {
                if (current is NamespaceDeclarationSyntax ns)
                    return ns.Name.ToString();

                if (current is FileScopedNamespaceDeclarationSyntax fns)
                    return fns.Name.ToString();

                current = current.Parent;
            }

            return null;
        }

        private static string? GetContainingTypeName(SyntaxNode node)
        {
            var current = node.Parent;
            while (current != null)
            {
                if (current is TypeDeclarationSyntax t)
                    return t.Identifier.ValueText;

                current = current.Parent;
            }

            return null;
        }

        private static void EnrichArtifactWithContext(SqlArtifact artifact, string relPath, int lineNumber, MethodContext? ctx)
        {
            if (artifact == null)
                return;

            // These fields are optional and primarily intended for inline SQL.
            artifact.RelativePath = relPath;
            artifact.LineNumber = lineNumber;

            if (ctx == null)
                return;

            if (!string.IsNullOrEmpty(ctx.Namespace))
                artifact.Namespace = ctx.Namespace;

            if (!string.IsNullOrEmpty(ctx.TypeFullName))
                artifact.TypeFullName = ctx.TypeFullName;

            if (!string.IsNullOrEmpty(ctx.MethodFullName))
                artifact.MethodFullName = ctx.MethodFullName;
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

        private static string MakeRelativePath(string root, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(root))
                return fullPath;

            try
            {
                var rootDir = AppendDirSep(Path.GetFullPath(root));
                var baseUri = new Uri(rootDir);
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
