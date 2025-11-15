using System;
using System.Linq;
using System.Threading;
using RoslynIndexer.Core.Sql.EfMigrations;

namespace RoslynIndexer.Tests.EfMigrations
{
    /// <summary>
    /// Test-only extension that maps EfMigrationAnalyzer output onto TestGraph.
    /// Keeps core analyzer free of graph concerns.
    /// </summary>
    public static class EfMigrationAnalyzerGraphExtensions
    {
        public static void AppendToGraph(
            this EfMigrationAnalyzer analyzer,
            string migrationsRoot,
            TestGraph graph,
            string repoRoot = null,
            CancellationToken cancellationToken = default)
        {
            if (analyzer == null)
                throw new ArgumentNullException(nameof(analyzer));
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));
            if (string.IsNullOrWhiteSpace(migrationsRoot))
                return;

            var infos = analyzer.Analyze(
                migrationsRoot,
                repoRoot ?? migrationsRoot,
                cancellationToken);

            foreach (var mig in infos)
            {
                var migrationKey = "csharp:" + mig.ClassName + "|MIGRATION";

                if (!graph.Nodes.ContainsKey(migrationKey))
                {
                    graph.Nodes[migrationKey] = new TestGraphNode
                    {
                        Key = migrationKey,
                        Kind = "MIGRATION",
                        Name = mig.ClassName,
                        Schema = "csharp",
                        File = mig.FileRelativePath,
                        Domain = "code"
                    };
                }

                foreach (var op in mig.UpOperations ?? Enumerable.Empty<EfMigrationOperation>())
                {
                    var relation = MapRelation(op.Kind);
                    if (relation == null)
                        continue;

                    if (string.IsNullOrWhiteSpace(op.Table))
                        continue;

                    var tableKey = ResolveTableKey(graph, op.Table);

                    if (!graph.Nodes.ContainsKey(tableKey))
                    {
                        // Create TABLE node if it does not exist yet
                        var (schema, name) = SplitSchemaAndName(tableKey);

                        graph.Nodes[tableKey] = new TestGraphNode
                        {
                            Key = tableKey,
                            Kind = "TABLE",
                            Name = name,
                            Schema = schema,
                            File = mig.FileRelativePath,
                            Domain = "sql"
                        };
                    }

                    graph.Edges.Add(new TestGraphEdge
                    {
                        From = migrationKey,
                        To = tableKey,
                        Relation = relation,
                        ToKind = "TABLE",
                        File = mig.FileRelativePath
                    });
                }
            }
        }

        private static string MapRelation(EfMigrationOperationKind kind)
        {
            switch (kind)
            {
                case EfMigrationOperationKind.CreateTable:
                case EfMigrationOperationKind.DropTable:
                case EfMigrationOperationKind.TouchTable:
                case EfMigrationOperationKind.CreateIndex:
                case EfMigrationOperationKind.DropIndex:
                    return "SchemaChange";

                case EfMigrationOperationKind.DataChange:
                    return "DataChange";

                case EfMigrationOperationKind.RawSql:
                case EfMigrationOperationKind.Unknown:
                default:
                    // For tests we ignore RawSql / Unknown.
                    return null;
            }
        }

        /// <summary>
        /// Resolve final TABLE key in the graph for a logical entity name.
        /// Tries plural first (dbo.Posts|TABLE), then singular (dbo.Post|TABLE),
        /// then falls back to plural if nothing exists.
        /// </summary>
        private static string ResolveTableKey(TestGraph graph, string logicalName)
        {
            var pluralKey = "dbo." + logicalName + "s|TABLE";
            var singularKey = "dbo." + logicalName + "|TABLE";

            if (graph.Nodes.ContainsKey(pluralKey))
                return pluralKey;

            if (graph.Nodes.ContainsKey(singularKey))
                return singularKey;

            // Default: plural, to match typical code-first conventions and our tests.
            return pluralKey;
        }

        private static (string schema, string name) SplitSchemaAndName(string key)
        {
            // key format: "schema.name|TABLE"
            var pipeIdx = key.IndexOf('|');
            var core = pipeIdx >= 0 ? key.Substring(0, pipeIdx) : key;
            var dotIdx = core.IndexOf('.');
            if (dotIdx < 0)
                return ("dbo", core);

            var schema = core.Substring(0, dotIdx);
            var name = core.Substring(dotIdx + 1);
            return (schema, name);
        }
    }
}
