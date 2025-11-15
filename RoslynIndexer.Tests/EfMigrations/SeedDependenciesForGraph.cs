// RoslynIndexer.Tests/EfMigrations/SeedDependenciesForGraph.cs
using System;
using System.Collections.Generic;
using System.IO;

namespace RoslynIndexer.Tests.EfMigrations
{
    /// <summary>
    /// In-memory representation of a SQL/EF dependency graph used in tests.
    /// This mimics nodes.csv / edges.csv / graph.json content.
    /// </summary>
    public sealed class TestGraph
    {
        public Dictionary<string, TestGraphNode> Nodes { get; } =
            new Dictionary<string, TestGraphNode>(StringComparer.OrdinalIgnoreCase);

        public List<TestGraphEdge> Edges { get; } = new List<TestGraphEdge>();
    }

    public sealed class TestGraphNode
    {
        public string Key { get; set; }           // e.g. "dbo.Blogs|TABLE"
        public string Kind { get; set; }          // e.g. "TABLE", "PROC", "MIGRATION"
        public string Name { get; set; }          // e.g. "Blogs"
        public string Schema { get; set; }        // e.g. "dbo" or "csharp"
        public string File { get; set; }          // relative file path for tests
        public string Domain { get; set; }        // optional: logical domain or module
    }

    public sealed class TestGraphEdge
    {
        public string From { get; set; }          // e.g. "dbo.Blogs|TABLE" or "csharp:Initial_20250101|MIGRATION"
        public string To { get; set; }            // e.g. "dbo.Posts|TABLE"
        public string Relation { get; set; }      // e.g. "ReadsFrom", "WritesTo", "SchemaChange", "DataChange"
        public string ToKind { get; set; }        // e.g. "TABLE"
        public string File { get; set; }          // relative file path for tests
    }

    /// <summary>
    /// Seeds an in-memory graph that simulates what pure SQL + EF CodeFirst would produce
    /// before we add EF migrations.
    /// </summary>
    public static class SeedDependenciesForGraph
    {
        /// <summary>
        /// Create a simple graph with two tables (Blogs, Posts) and one dependency edge.
        /// repoRoot is only used to build fake relative file paths.
        /// </summary>
        public static TestGraph SeedBlogSample(string repoRoot)
        {
            var graph = new TestGraph();

            // We use fake file paths just to exercise file serialization.
            var sqlFile = MakeRelPath(repoRoot, Path.Combine(repoRoot, "Sql", "01_Create_Blogs_Posts.sql"));
            var codeFile = MakeRelPath(repoRoot, Path.Combine(repoRoot, "Code", "BlogRepository.cs"));

            // TABLE: dbo.Blogs
            var blogsKey = "dbo.Blogs|TABLE";
            graph.Nodes[blogsKey] = new TestGraphNode
            {
                Key = blogsKey,
                Kind = "TABLE",
                Name = "Blogs",
                Schema = "dbo",
                File = sqlFile,
                Domain = "blog"
            };

            // TABLE: dbo.Posts
            var postsKey = "dbo.Posts|TABLE";
            graph.Nodes[postsKey] = new TestGraphNode
            {
                Key = postsKey,
                Kind = "TABLE",
                Name = "Posts",
                Schema = "dbo",
                File = sqlFile,
                Domain = "blog"
            };

            // Simulate that some C# repository writes to Posts
            var repoKey = "csharp:BlogRepository.CreatePost|METHOD";
            graph.Nodes[repoKey] = new TestGraphNode
            {
                Key = repoKey,
                Kind = "METHOD",
                Name = "CreatePost",
                Schema = "csharp",
                File = codeFile,
                Domain = "code"
            };

            graph.Edges.Add(new TestGraphEdge
            {
                From = repoKey,
                To = postsKey,
                Relation = "WritesTo",
                ToKind = "TABLE",
                File = codeFile
            });

            return graph;
        }

        private static string MakeRelPath(string root, string fullPath)
        {
            // Very small helper for tests – no need to be bulletproof.
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(fullPath);

            if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return full.Substring(rootFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return full.Replace('\\', '/');
        }
    }
}
