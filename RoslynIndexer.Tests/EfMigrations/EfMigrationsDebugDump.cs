using System;
using System.Linq;

namespace RoslynIndexer.Tests.EfMigrations
{
    internal static class EfMigrationsDebugDump
    {
        public static void DumpGraph(TestGraph graph)
        {
            Console.WriteLine("====== TEST GRAPH DUMP ======");

            Console.WriteLine("NODES:");
            foreach (var node in graph.Nodes.Values.OrderBy(n => n.Key))
            {
                Console.WriteLine($"  {node.Key} | kind={node.Kind} | schema={node.Schema} | name={node.Name} | file={node.File}");
            }

            Console.WriteLine("EDGES:");
            foreach (var edge in graph.Edges.OrderBy(e => e.From).ThenBy(e => e.To))
            {
                Console.WriteLine($"  {edge.From} -> {edge.To} | relation={edge.Relation} | toKind={edge.ToKind} | file={edge.File}");
            }

            Console.WriteLine("====== END GRAPH DUMP ======");
        }

        public static void DumpEdgesForMigration(TestGraph graph, string migrationClassName)
        {
            var fromKey = "csharp:" + migrationClassName + "|MIGRATION";

            Console.WriteLine($"====== EDGES FOR MIGRATION {migrationClassName} ({fromKey}) ======");

            var edges = graph.Edges.Where(e => string.Equals(e.From, fromKey, StringComparison.Ordinal)).ToList();
            if (!edges.Any())
            {
                Console.WriteLine("  (no edges from this migration)");
            }
            else
            {
                foreach (var edge in edges.OrderBy(e => e.To))
                {
                    Console.WriteLine($"  {edge.From} -> {edge.To} | relation={edge.Relation}");
                }
            }

            Console.WriteLine("====== END EDGES FOR MIGRATION ======");
        }
    }
}
