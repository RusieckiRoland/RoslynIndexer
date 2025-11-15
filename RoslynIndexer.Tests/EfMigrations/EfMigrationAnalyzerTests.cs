// RoslynIndexer.Tests/EfMigrations/EfMigrationAnalyzerTests.cs
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using RoslynIndexer.Core.Sql.EfMigrations;
using RoslynIndexer.Tests.Common;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RoslynIndexer.Tests.EfMigrations
{
    [TestClass]
    [TestCategory("Unit")]
    public class EfMigrationAnalyzerTests
    {
        [TestMethod]
        public void EfMigrations_Should_Append_MigrationNodes_AndEdges_To_Graph()
        {
            // Na czas rozwoju: brutalne włączenie logowania z poziomu testu.
            // Docelowo możesz to wyłączyć albo sterować tylko przez RI_TEST_VERBOSE.
            TestLog.Enabled = false;

            // Arrange
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RI_EF_MIG_ANALYZER_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);

            try
            {
                TestLog.Info("Test root: " + testRoot);

                // 1) Fake existing SQL+EF graph
                var baseGraph = SeedDependenciesForGraph.SeedBlogSample(testRoot);
                TestLog.Info("Seeded base blog graph (nodes + edges).");

                // 2) Seed sample migrations (C# files only)
                var migrationsRoot = SeedEfMigrations.CreateSampleMigrations(testRoot);
                TestLog.Info("Seeded sample migrations under: " + migrationsRoot);

                // 3) Analyzer under test
                var analyzer = new EfMigrationAnalyzer();

                // Act
                analyzer.AppendToGraph(migrationsRoot, baseGraph);
                TestLog.Info("EfMigrationAnalyzer.AppendToGraph completed.");

                // 4) Serialize to files like production (nodes.csv, edges.csv, graph.json)
                var outDir = Path.Combine(testRoot, "out_graph");
                Directory.CreateDirectory(outDir);
                SerializeTestGraph(outDir, baseGraph);
                TestLog.Info("Serialized test graph to: " + outDir);

                var edgesPath = Path.Combine(outDir, "edges.csv");
                var edgesCsv = File.ReadAllLines(edgesPath).ToList();

                TestLog.Info("==== edges.csv dump ====");
                foreach (var line in edgesCsv)
                    TestLog.Info(line);
                TestLog.Info("==== end edges.csv dump ====");

                //
                // ASSERTS
                //

                // 1) Kod aplikacyjny: BlogRepository.CreatePost -> dbo.Posts (WritesTo)
                bool hasRepositoryWrites = edgesCsv.Any(l =>
                    l.Contains("csharp:BlogRepository.CreatePost|METHOD") &&
                    l.Contains("dbo.Posts|TABLE") &&
                    l.Contains("WritesTo"));

                Assert.IsTrue(
                    hasRepositoryWrites,
                    "Expected WritesTo edge from BlogRepository.CreatePost method to dbo.Posts table.");

                // 2) Migracja Initial_20250101 robi SchemaChange na dbo.Blogs
                bool hasInitialBlogs = edgesCsv.Any(l =>
                    l.Contains("Initial_20250101|MIGRATION") &&
                    l.Contains("dbo.Blogs|TABLE") &&
                    l.Contains("SchemaChange"));

                Assert.IsTrue(
                    hasInitialBlogs,
                    "Expected SchemaChange edge from Initial_20250101 migration to dbo.Blogs table.");

                // 3) Migracja SeedPosts_20250105 robi DataChange na dbo.Posts
                bool hasSeedPostsData = edgesCsv.Any(l =>
                    l.Contains("SeedPosts_20250105|MIGRATION") &&
                    l.Contains("dbo.Posts|TABLE") &&
                    l.Contains("DataChange"));

                Assert.IsTrue(
                    hasSeedPostsData,
                    "Expected DataChange edge from SeedPosts_20250105 migration to dbo.Posts table.");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(testRoot))
                        Directory.Delete(testRoot, true);
                }
                catch
                {
                    // W testach nie panikujemy, jeśli sprzątanie temp się nie powiedzie.
                }
            }
        }

        /// <summary>
        /// Serializes TestGraph to nodes.csv, edges.csv and graph.json in a simple, production-like format.
        /// </summary>
        private static void SerializeTestGraph(string outDir, TestGraph graph)
        {
            var graphDir = outDir;

            var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
            var edgesCsvPath = Path.Combine(graphDir, "edges.csv");
            var graphJsonPath = Path.Combine(graphDir, "graph.json");

            // nodes.csv header
            var nodesLines = graph.Nodes.Values
                .OrderBy(n => n.Key)
                .Select(n => Csv(n.Key, n.Kind, n.Name, n.Schema, n.File, n.Domain))
                .ToList();
            nodesLines.Insert(0, "key,kind,name,schema,file,domain");
            File.WriteAllLines(nodesCsvPath, nodesLines, Encoding.UTF8);

            // edges.csv header
            var edgesLines = graph.Edges
                .OrderBy(e => e.From)
                .ThenBy(e => e.To)
                .Select(e => Csv(e.From, e.To, e.Relation, e.ToKind, e.File))
                .ToList();
            edgesLines.Insert(0, "from,to,relation,to_kind,file");
            File.WriteAllLines(edgesCsvPath, edgesLines, Encoding.UTF8);

            // graph.json – simple JSON mirror of the same data
            var jsonObj = new
            {
                nodes = graph.Nodes.Values.OrderBy(n => n.Key).ToArray(),
                edges = graph.Edges.OrderBy(e => e.From).ThenBy(e => e.To).ToArray()
            };
            File.WriteAllText(graphJsonPath, JsonConvert.SerializeObject(jsonObj, Formatting.Indented), Encoding.UTF8);
        }

        private static string Csv(params object[] vals)
        {
            return string.Join(",", vals.Select(v =>
            {
                var s = v?.ToString() ?? string.Empty;
                s = s.Replace("\"", "\"\"");
                return "\"" + s + "\"";
            }));
        }
    }
}
