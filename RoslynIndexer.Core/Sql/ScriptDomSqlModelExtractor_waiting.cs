using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using RoslynIndexer.Core.Abstractions;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Core.Sql
{
    /// <summary>
    /// Extracts SQL artifacts (PROC/VIEW/FUNC/TABLE/TRIGGER/SEQUENCE/TYPE) from *.sql files under paths.SqlPath.
    /// Minimal, safe for .NET Standard 2.0.
    /// </summary>
    public sealed class ScriptDomSqlModelExtractor_waiting : ISqlModelExtractor
    {
        public IEnumerable<SqlArtifact> Extract(RepoPaths paths)
        {
            // 2) Inline SQL in C# files under paths.InlineSqlPath.
            if (!string.IsNullOrWhiteSpace(paths.SqlPath))
            {
                var root = Path.GetFullPath(paths.SqlPath);
                if (Directory.Exists(root))
                {
                    var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git",".vs","bin","obj","Tools",
                "Change Scripts","ChangeScripts",
                "Initial Data","InitialData",
                "Snapshots"
            };

                    foreach (var file in EnumerateSql(root, ignore))
                    {
                        foreach (var art in ExtractFromFile(file))
                            yield return art;
                    }
                }
            }

            // 2) inline SQL w plikach C# pod paths.InlineSqlPath
            if (!string.IsNullOrWhiteSpace(paths.InlineSqlPath))
            {
                foreach (var art in InlineSqlScanner.ScanInlineSql(paths.InlineSqlPath))
                    yield return art;
            }
        }


        private static IEnumerable<string> EnumerateSql(string dir, HashSet<string> ignore)
        {
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (ignore.Contains(name)) continue;
                foreach (var f in EnumerateSql(d, ignore)) yield return f;
            }
            foreach (var f in Directory.EnumerateFiles(dir, "*.sql"))
                yield return f;
        }

        private static IEnumerable<SqlArtifact> ExtractFromFile(string path)
        {
            var parser = new TSql150Parser(initialQuotedIdentifiers: true);
            string sql = ReadAndPreprocess(path);

            IList<ParseError> errors;
            TSqlFragment fragment;
            using (var sr = new StringReader(sql))
                fragment = parser.Parse(sr, out errors);

            // Be tolerant: even with errors we try to collect definitions.
            var script = fragment as TSqlScript;
            if (script == null) yield break;

            var collector = new DefineCollector();
            script.Accept(collector);

            foreach (var def in collector.Defines)
            {
                var schema = def.Item1.SchemaIdentifier != null ? def.Item1.SchemaIdentifier.Value : "dbo";
                var name = def.Item1.BaseIdentifier != null ? def.Item1.BaseIdentifier.Value : "(anon)";
                var kind = def.Item2;

                var id = schema + "." + name + "|" + kind;
                yield return new SqlArtifact(sourcePath: path, artifactKind: kind, identifier: id);
            }
        }

        private static string ReadAndPreprocess(string path)
        {
            var text = File.ReadAllText(path);
            var lines = new List<string>();
            foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                // Drop sqlcmd directives (e.g. :r, :setvar) which ScriptDom doesn't parse
                if (Regex.IsMatch(raw, @"^\s*:(r|setvar|connect|on\s+error\s+exit)\b", RegexOptions.IgnoreCase))
                    continue;
                lines.Add(raw);
            }
            var cleaned = string.Join("\n", lines);
            // Replace $(Var) tokens with neutral literal to keep parser happy
            cleaned = Regex.Replace(cleaned, @"\$\([^)]+\)", "0");
            return cleaned;
        }

        /// <summary>Collects CREATE/ALTER definitions only.</summary>
        private sealed class DefineCollector : TSqlFragmentVisitor
        {
            public readonly List<Tuple<SchemaObjectName, string>> Defines = new List<Tuple<SchemaObjectName, string>>();

            private void Add(SchemaObjectName n, string kind)
            {
                if (n == null || n.BaseIdentifier == null) return;
                Defines.Add(Tuple.Create(n, kind));
            }

            public override void Visit(CreateTableStatement node) => Add(node.SchemaObjectName, "TABLE");
            public override void Visit(CreateViewStatement node) => Add(node.SchemaObjectName, "VIEW");
            public override void Visit(CreateProcedureStatement node) { var r = node.ProcedureReference; if (r?.Name != null) Add(r.Name, "PROC"); }
            public override void Visit(CreateFunctionStatement node) => Add(node.Name, "FUNC");
            public override void Visit(CreateSequenceStatement node) => Add(node.Name, "SEQUENCE");
            public override void Visit(CreateTypeTableStatement node) => Add(node.Name, "TYPE");
            public override void Visit(CreateTypeUddtStatement node) => Add(node.Name, "TYPE");
            public override void Visit(CreateTriggerStatement node) => Add(node.Name, "TRIGGER");

            public override void Visit(AlterViewStatement node) { if (node.SchemaObjectName != null) Add(node.SchemaObjectName, "VIEW"); }
            public override void Visit(AlterProcedureStatement node) { var r = node.ProcedureReference; if (r?.Name != null) Add(r.Name, "PROC"); }
            public override void Visit(AlterFunctionStatement node) { if (node.Name != null) Add(node.Name, "FUNC"); }
            public override void Visit(AlterTableAddTableElementStatement node) { if (node.SchemaObjectName != null) Add(node.SchemaObjectName, "TABLE"); }
        }
    }
}
