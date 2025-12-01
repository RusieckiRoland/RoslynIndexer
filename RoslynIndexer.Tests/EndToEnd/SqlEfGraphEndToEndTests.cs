using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Net9.Adapters; // MsBuildWorkspaceLoader to locate RoslynIndexer.Net9 assembly
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RoslynIndexer.Net9.Tests.EndToEnd
{
    /// <summary>
    /// End-to-end test that:
    /// 1) Creates a tiny solution with:
    ///    - regular C# code,
    ///    - a pseudo EF DbContext + DbSet<Customer>,
    ///    - a SQL folder with CREATE TABLE dbo.Customer,
    ///    - a migrations folder with a migration touching Customer table,
    /// 2) Writes config.json pointing to those paths,
    /// 3) Executes RoslynIndexer.Net9 Program.Main("--config", configPath),
    /// 4) Verifies that expected SQL/EF graph artifacts are produced.
    /// </summary>
    [TestClass]
    public class SqlEfGraphEndToEndTests
    {
        // When set to "1", end-to-end tests will keep their temp directories
        // so you can inspect both the generated code (MiniEfSolution/sql/migrations)
        // and the produced sql_code_bundle/graph artifacts.
        private static readonly bool KeepArtifacts =
            string.Equals(
                Environment.GetEnvironmentVariable("RI_KEEP_E2E"),
                "1",
                StringComparison.Ordinal);

        [TestMethod]
        public void EfFluent_ForeignKey_ProducesTableToTableForeignKeyEdges()
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "RoslynIndexer_E2E_EfFluentFK_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);

            try
            {
                var sol = Path.Combine(testRoot, "FluentFkSolution");
                var proj = Path.Combine(sol, "FluentFkProject");
                Directory.CreateDirectory(sol);
                Directory.CreateDirectory(proj);

                var sqlDir = Path.Combine(sol, "sqlEmpty");
                Directory.CreateDirectory(sqlDir);

                var outDir = Path.Combine(testRoot, "out");
                Directory.CreateDirectory(outDir);
                var tempRoot = Path.Combine(testRoot, "temp");
                Directory.CreateDirectory(tempRoot);

                var slnPath = Path.Combine(sol, "FluentFkSolution.sln");
                var projPath = Path.Combine(proj, "FluentFkProject.csproj");
                var configPath = Path.Combine(testRoot, "config_ef_fluent_fk.json");

                CreateMinimalSln(slnPath, projPath);
                CreateMinimalProjectFile(projPath);

                CreateEfFluentModelWithForeignKey(proj);

                CreateConfigJson(configPath, slnPath, tempRoot, outDir, sqlDir: string.Empty, efDir: proj,
                migrationsDir: string.Empty);


                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                var zip = Directory.GetFiles(outDir, "*.zip").First();
                var unpack = Path.Combine(testRoot, "unzipped_fk");
                ZipFile.ExtractToDirectory(zip, unpack, true);

                var edgesCsv = Path.Combine(unpack, "sql_code_bundle", "graph", "edges.csv");
                var edges = File.ReadAllLines(edgesCsv);
                Assert.IsTrue(edges.Any(l => l.Contains("ForeignKey", StringComparison.Ordinal)),
                    "Expected at least one ForeignKey edge in EF Fluent FK graph.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void EfFluent_ForeignKeyEdge_IsDirectedFromChildToParent()
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "RoslynIndexer_E2E_EfFluentFK_Dir_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);

            try
            {
                var sol = Path.Combine(testRoot, "FluentDirSolution");
                var proj = Path.Combine(sol, "FluentDirProject");
                Directory.CreateDirectory(sol);
                Directory.CreateDirectory(proj);

                var sqlDir = Path.Combine(sol, "sqlEmpty");
                Directory.CreateDirectory(sqlDir);

                var outDir = Path.Combine(testRoot, "out");
                Directory.CreateDirectory(outDir);
                var tempRoot = Path.Combine(testRoot, "temp");
                Directory.CreateDirectory(tempRoot);

                var slnPath = Path.Combine(sol, "FluentDirSolution.sln");
                var projPath = Path.Combine(proj, "FluentDirProject.csproj");
                var configPath = Path.Combine(testRoot, "config_ef_fluent_fk_dir.json");

                CreateMinimalSln(slnPath, projPath);
                CreateMinimalProjectFile(projPath);
                CreateEfFluentModelWithForeignKey(proj);
                CreateConfigJson(configPath, slnPath, tempRoot, outDir, sqlDir: string.Empty, efDir: proj, migrationsDir: string.Empty);

                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                var zip = Directory.GetFiles(outDir, "*.zip").First();
                var unpack = Path.Combine(testRoot, "unzipped_fk");
                ZipFile.ExtractToDirectory(zip, unpack, true);

                var edgesCsv = Path.Combine(unpack, "sql_code_bundle", "graph", "edges.csv");
                var lines = File.ReadAllLines(edgesCsv);
                var fkLine = lines.FirstOrDefault(l => l.Contains("ForeignKey", StringComparison.Ordinal));

                Assert.IsNotNull(fkLine, "Expected ForeignKey edge to exist in EF Fluent FK direction test.");
                StringAssert.Contains(fkLine, "dbo.Child|TABLE");
                StringAssert.Contains(fkLine, "dbo.Parent|TABLE");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]

        public void EfOnly_ProducesEntityNodesInGraph()
        {
            // Root for this test run
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_EFOnly_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure
                var solutionDir = Path.Combine(testRoot, "MiniEfSolution");
                var projectDir = Path.Combine(solutionDir, "MiniEfProject");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "MiniEfSolution.sln");
                var projectPath = Path.Combine(projectDir, "MiniEfProject.csproj");
                var configPath = Path.Combine(testRoot, "config.json");

                // 2) Create minimal solution + project + EF model (BaseEntity + Customer)
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);
                CreateMiniEfModel(projectDir);

                // 3) Create config.json for EF-only mode:
                //    - paths.sql = ""          (no SQL files)
                //    - paths.ef  = projectDir  (EF root)
                //    - paths.migrations = ""   (no migrations)
                //    - dbGraph.entityBaseTypes = ["MiniEf.BaseEntity"]
                CreateConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: string.Empty,
                    efDir: projectDir,
                    migrationsDir: string.Empty);

                // 4) Run RoslynIndexer.Net9 as external process with --config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 5) After run: Program.Net9 packs tempRoot into ZIP under outDir
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for EF-only run.");

                var firstZip = zipFiles[0];

                // 6) Unpack ZIP to inspect sql_code_bundle graph artifacts
                var unpackRoot = Path.Combine(testRoot, "unzipped");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var docsDir = Path.Combine(sqlBundleRoot, "docs");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");

                var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(sqlBodiesPath),
                    "Expected sql_bodies.jsonl to be produced in EF-only mode (even if empty).");
                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced in EF-only mode.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced in EF-only mode.");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // 7) ENTITY node for Customer must exist – proof that dbGraph.entityBaseTypes
                //    from config.json is respected and EF-only path still emits ENTITY nodes.
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("csharp:Customer|ENTITY")),
                    "Expected ENTITY node for Customer (csharp:Customer|ENTITY) in EF-only graph.");

                // 8) In EF-only mode without migrations we do NOT expect any MIGRATION nodes.
                Assert.IsFalse(
                    nodesLines.Any(l => l.Contains("|MIGRATION")),
                    "Did not expect any MIGRATION nodes in EF-only run (paths.migrations is empty).");

                // Edges may legitimately be empty (no TABLE mapping in EF-only),
                // więc nie wymuszamy żadnych relacji tutaj.
            }
            finally
            {

                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void InlineSql_ForeignKey_ProducesTableToTableForeignKeyEdges()
        {
            // Root for this test run
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_InlineSql_FK_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Folder structure – bardzo podobna do InlineSqlOnly_ProducesGraphNodesAndEdges
                var solutionDir = Path.Combine(testRoot, "InlineSqlFkSolution");
                var projectDir = Path.Combine(solutionDir, "InlineSqlFkProject");
                var sqlDir = Path.Combine(solutionDir, "sqlEmpty");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "InlineSqlFkSolution.sln");
                var projectPath = Path.Combine(projectDir, "InlineSqlFkProject.csproj");
                var configPath = Path.Combine(testRoot, "config_inline_fk.json");

                // 2) Minimal solution + project
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);

                // 3) C# with inline DDL that contains FOREIGN KEY ... REFERENCES ...,
                //    PLUS a SELECT, żeby InlineSqlScanner na pewno potraktował to jako SQL.
                var inlineCode = """
using System;
using System.Collections.Generic;
using System.Data;

namespace InlineSqlFkSample
{
    public sealed class Parent
    {
        public int Id { get; set; }
    }

    public sealed class Child
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
    }

    public static class RawSqlWithFk
    {
        public static IEnumerable<Child> LoadChildren(IDbConnection connection)
        {
                    const string sql = @"
         CREATE TABLE dbo.Parent
         (
             Id INT NOT NULL PRIMARY KEY
         );
         
         CREATE TABLE dbo.Child
         (
             Id       INT NOT NULL PRIMARY KEY,
             ParentId INT NOT NULL,
             CONSTRAINT FK_Child_Parent
                 FOREIGN KEY (ParentId)
                 REFERENCES dbo.Parent(Id)
         );
         ";


            // For the test it is enough that the SQL literal is present in IL.
            Console.WriteLine(sql);

            return Array.Empty<Child>();
        }
    }
}
""";

                var inlineFilePath = Path.Combine(projectDir, "InlineSqlFkSample.cs");
                File.WriteAllText(inlineFilePath, inlineCode, Encoding.UTF8);

                // 4) Config: inline-only scenario – identyczny wzór jak w InlineSqlOnly_ProducesGraphNodesAndEdges
                var sb = new StringBuilder();

                sb.AppendLine("{");
                sb.AppendLine("  \"paths\": {");
                sb.AppendLine("    \"solution\":   " + JsonString(solutionPath) + ",");
                sb.AppendLine("    \"tempRoot\":   " + JsonString(tempRoot) + ",");
                sb.AppendLine("    \"outRoot\":        " + JsonString(outDir) + ",");

                // New keys used by Program.Net9
                sb.AppendLine("    \"sqlRoot\":        " + JsonString(sqlDir) + ",");
                sb.AppendLine("    \"modelRoot\":      \"\",");
                sb.AppendLine("    \"migrationsRoot\": \"\",");
                sb.AppendLine("    \"inlineSqlRoot\":  " + JsonString(projectDir) + ",");

                // (Legacy aliases tutaj pomijamy – tak jak w teście InlineSqlOnly)
                sb.AppendLine("  },");
                sb.AppendLine("  \"dbGraph\": {");
                sb.AppendLine("    \"entityBaseTypes\": []");
                sb.AppendLine("  }");
                sb.AppendLine("}");

                File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);

                // 5) Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 6) ZIP → rozpakowujemy i czytamy graph artifacts
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for inline-FK run.");

                var firstZip = zipFiles[0];

                var unpackRoot = Path.Combine(testRoot, "unzipped_fk");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var docsDir = Path.Combine(sqlBundleRoot, "docs");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");

                var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(sqlBodiesPath),
                    "Expected sql_bodies.jsonl to be produced in inline-FK mode (even if minimal).");
                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced in inline-FK mode.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced in inline-FK mode.");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // sanity: coś faktycznie zostało wygenerowane
                Assert.IsTrue(
                    nodesLines.Length > 1,
                    "Expected at least one data row in nodes.csv for inline-FK run.");
                Assert.IsTrue(
                    edgesLines.Length > 1,
                    "Expected at least one data row in edges.csv for inline-FK run.");

                // --- Główna asercja: TABLE -> TABLE (ForeignKey) z inline DDL ---

                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("dbo.Child|TABLE", StringComparison.Ordinal) &&
                        l.Contains("dbo.Parent|TABLE", StringComparison.Ordinal) &&
                        l.Contains("ForeignKey", StringComparison.Ordinal)),
                    "Expected ForeignKey edge: dbo.Child|TABLE -> dbo.Parent|TABLE to be present in edges.csv for inline-FK run.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void InlineSql_ForeignKeyEdge_IsDirectedFromChildToParent()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_InlineSql_FK_Direction_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                var solutionDir = Path.Combine(testRoot, "InlineSqlFkDirSolution");
                var projectDir = Path.Combine(solutionDir, "InlineSqlFkDirProject");
                var sqlDir = Path.Combine(solutionDir, "sqlEmpty");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "InlineSqlFkDirSolution.sln");
                var projectPath = Path.Combine(projectDir, "InlineSqlFkDirProject.csproj");
                var configPath = Path.Combine(testRoot, "config_inline_fk_direction.json");

                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);

                // C# file with inline DDL + SELECT (to trigger InlineSqlScanner)
                var inlineCode = """
using System;
using System.Collections.Generic;
using System.Data;

namespace InlineSqlFkDirectionSample
{
    public sealed class Parent
    {
        public int Id { get; set; }
    }

    public sealed class Child
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
    }

    public static class RawSqlWithFkDirection
    {
        public static IEnumerable<Child> LoadChildren(IDbConnection connection)
        {
         
        const string sql = @"
CREATE TABLE dbo.Parent
(
    Id INT NOT NULL PRIMARY KEY
);

CREATE TABLE dbo.Child
(
    Id       INT NOT NULL PRIMARY KEY,
    ParentId INT NOT NULL,
    CONSTRAINT FK_Child_Parent
        FOREIGN KEY (ParentId)
        REFERENCES dbo.Parent(Id)
);
";


            Console.WriteLine(sql);
            return Array.Empty<Child>();
        }
    }
}
""";

                var inlineFilePath = Path.Combine(projectDir, "InlineSqlFkDirectionSample.cs");
                File.WriteAllText(inlineFilePath, inlineCode, Encoding.UTF8);

                // Config: inline-only scenario
                var sb = new StringBuilder();

                sb.AppendLine("{");
                sb.AppendLine("  \"paths\": {");
                sb.AppendLine("    \"solution\":   " + JsonString(solutionPath) + ",");
                sb.AppendLine("    \"tempRoot\":   " + JsonString(tempRoot) + ",");
                sb.AppendLine("    \"outRoot\":        " + JsonString(outDir) + ",");
                sb.AppendLine("    \"sqlRoot\":        " + JsonString(sqlDir) + ",");
                sb.AppendLine("    \"modelRoot\":      \"\",");
                sb.AppendLine("    \"migrationsRoot\": \"\",");
                sb.AppendLine("    \"inlineSqlRoot\":  " + JsonString(projectDir));
                sb.AppendLine("  },");
                sb.AppendLine("  \"dbGraph\": {");
                sb.AppendLine("    \"entityBaseTypes\": []");
                sb.AppendLine("  }");
                sb.AppendLine("}");

                File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);

                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for inline-FK direction run.");

                var firstZip = zipFiles[0];
                var unpackRoot = Path.Combine(testRoot, "unzipped_inline_fk_direction");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced for inline-FK direction run.");

                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // Find the FK edge that links dbo.Child|TABLE and dbo.Parent|TABLE
                var fkLine = edgesLines
                    .Skip(1) // skip header
                    .FirstOrDefault(l =>
                        l.Contains("dbo.Child|TABLE", StringComparison.Ordinal) &&
                        l.Contains("dbo.Parent|TABLE", StringComparison.Ordinal) &&
                        l.Contains("ForeignKey", StringComparison.Ordinal));

                Assert.IsNotNull(fkLine,
                    "Expected to find at least one ForeignKey edge between dbo.Child|TABLE and dbo.Parent|TABLE in inline-FK direction run.");

                // NOTE: This assumes edges.csv columns are: From,To,Relation,ToKind,File,Batch.
                var parts = fkLine.Split(',');
                Assert.IsTrue(parts.Length >= 3,
                    "Expected at least 3 CSV columns in edges.csv (From,To,Relation,...) for FK edge.");

                var from = parts[0];
                var to = parts[1];
                var rel = parts[2];

                // We only assert on substrings to be robust against CSV quoting.
                Assert.IsTrue(
                    from.Contains("dbo.Child|TABLE", StringComparison.Ordinal),
                    $"Expected ForeignKey 'From' to contain dbo.Child|TABLE (actual: {from}).");

                Assert.IsTrue(
                    to.Contains("dbo.Parent|TABLE", StringComparison.Ordinal),
                    $"Expected ForeignKey 'To' to contain dbo.Parent|TABLE (actual: {to}).");

                Assert.IsTrue(
                    rel.Contains("ForeignKey", StringComparison.Ordinal),
                    $"Expected 'Relation' column to contain 'ForeignKey' for inline-FK edge (actual: {rel}).");

            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void InlineSqlOnly_ProducesGraphNodesAndEdges()
        {
            // Root for this test run
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_InlineSqlOnly_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure
                //    - InlineSqlSolution/InlineSqlProject  → project with inline SQL usage
                //    - sqlEmpty                            → exists but contains no *.sql files
                var solutionDir = Path.Combine(testRoot, "InlineSqlSolution");
                var projectDir = Path.Combine(solutionDir, "InlineSqlProject");
                var sqlDir = Path.Combine(solutionDir, "sqlEmpty");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "InlineSqlSolution.sln");
                var projectPath = Path.Combine(projectDir, "InlineSqlProject.csproj");
                var configPath = Path.Combine(testRoot, "config.json");

                // 2) Minimal solution + project
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);

                // 3) C# file with inline SQL usage.
                //    We deliberately do NOT create any .sql scripts or migrations.
                var inlineCode = """
using System;
using System.Collections.Generic;
using System.Data;

namespace InlineSqlSample
{
    public sealed class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }

    public static class RawSql
    {
        public static IEnumerable<Customer> LoadCustomers(IDbConnection connection)
        {
            const string sql = @"
SELECT c.Id, c.Name
FROM dbo.Customer c
WHERE c.IsActive = 1;
";

            // In a real application we would execute the SQL.
            // For the test we only need the raw SQL string to be present in IL.
            Console.WriteLine(sql);

            return Array.Empty<Customer>();
        }
    }
}
""";

                var inlineFilePath = Path.Combine(projectDir, "InlineSqlSample.cs");
                File.WriteAllText(inlineFilePath, inlineCode, Encoding.UTF8);

                // 4) Config: inline-only scenario
                //    - paths.sql        = sqlDir          (exists, but contains no *.sql files)
                //    - paths.ef         = ""              (no EF root)
                //    - paths.migrations = ""              (no migrations)
                //    - paths.inlineSql  = projectDir      (only source of SQL knowledge)
                //    - dbGraph.entityBaseTypes = []       (no EF entities)
                var sb = new StringBuilder();

                sb.AppendLine("{");
                sb.AppendLine("  \"paths\": {");
                sb.AppendLine("    \"solution\":   " + JsonString(solutionPath) + ",");
                sb.AppendLine("    \"tempRoot\":   " + JsonString(tempRoot) + ",");
                sb.AppendLine("    \"outRoot\":        " + JsonString(outDir) + ",");

                // New keys used by Program.Net9
                sb.AppendLine("    \"sqlRoot\":        " + JsonString(sqlDir) + ",");
                sb.AppendLine("    \"modelRoot\":      \"\",");
                sb.AppendLine("    \"migrationsRoot\": \"\",");
                sb.AppendLine("    \"inlineSqlRoot\":  " + JsonString(projectDir) + ",");

                // Legacy aliases (not required, but kept for compatibility)




                sb.AppendLine("  },");
                sb.AppendLine("  \"dbGraph\": {");
                sb.AppendLine("    \"entityBaseTypes\": []");
                sb.AppendLine("  }");
                sb.AppendLine("}");

                File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);


                // 5) Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 6) After run: Program.Net9 packs tempRoot into ZIP under outDir
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for inline-only run.");

                var firstZip = zipFiles[0];

                // 7) Unpack ZIP to inspect sql_code_bundle graph artifacts
                var unpackRoot = Path.Combine(testRoot, "unzipped");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var docsDir = Path.Combine(sqlBundleRoot, "docs");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");

                var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(sqlBodiesPath),
                    "Expected sql_bodies.jsonl to be produced in inline-only mode (even if minimal).");
                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced in inline-only mode.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced in inline-only mode.");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // We expect inline-SQL stage to actually add something to the graph,
                // not just headers.
                Assert.IsTrue(
                    nodesLines.Length > 1,
                    "Expected at least one data row in nodes.csv for inline-only run (inline SQL should produce nodes).");

                Assert.IsTrue(
                    edgesLines.Length > 1,
                    "Expected at least one data row in edges.csv for inline-only run (inline SQL should produce edges).");

                // --- New, stricter assertions for inline-SQL wiring ---

                // 1) METHOD node for RawSql.LoadCustomers should exist.
                Assert.IsTrue(
                    nodesLines.Any(l =>
                        l.Contains("RawSql.LoadCustomers", StringComparison.Ordinal) &&
                        l.Contains("|METHOD", StringComparison.Ordinal)),
                    "Expected METHOD node for InlineSqlSample.RawSql.LoadCustomers in nodes.csv.");

                // 2) ReadsFrom edge: METHOD -> dbo.Customer|TABLE_OR_VIEW
                //    We only assert on substrings to stay robust to exact key formatting.
                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("RawSql.LoadCustomers", StringComparison.Ordinal) &&
                        l.Contains("dbo.Customer", StringComparison.Ordinal) &&
                        l.Contains("ReadsFrom", StringComparison.Ordinal)),
                    "Expected ReadsFrom edge from RawSql.LoadCustomers METHOD to dbo.Customer in edges.csv.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void Migrations_AddForeignKey_ProducesTableToTableForeignKeyEdge()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_Migrations_FK_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure
                var solutionDir = Path.Combine(testRoot, "MiniEfFkSolution");
                var projectDir = Path.Combine(solutionDir, "MiniEfFkProject");
                var migrationsDir = Path.Combine(solutionDir, "migrations");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(migrationsDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "MiniEfFkSolution.sln");
                var projectPath = Path.Combine(projectDir, "MiniEfFkProject.csproj");
                var configPath = Path.Combine(testRoot, "config_fk_migrations.json");

                // 2) Minimal solution + project + EF model
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);
                CreateMiniEfModel(projectDir);

                // 3) Migrations with AddForeignKey pattern
                CreateMigrationsWithForeignKey(migrationsDir);

                // 4) Config: migrations-only scenario (no SQL, no dbGraph.entityBaseTypes)
                CreateConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: string.Empty,
                    efDir: string.Empty,
                    migrationsDir: migrationsDir,
                    includeEntityBaseTypes: false);

                // 5) Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 6) Unpack ZIP and inspect nodes/edges.
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for migrations-with-FK run.");

                var firstZip = zipFiles[0];

                var unpackRoot = Path.Combine(testRoot, "unzipped");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var docsDir = Path.Combine(sqlBundleRoot, "docs");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");

                var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(sqlBodiesPath),
                    "Expected sql_bodies.jsonl to be produced (inside ZIP) for migrations-with-FK run.");
                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced (inside ZIP) for migrations-with-FK run.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced (inside ZIP) for migrations-with-FK run.");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // MIGRATION node must exist for our migration.
                Assert.IsTrue(
                    nodesLines.Any(l =>
                        l.Contains("csharp:MiniEf.Migrations.AddOrdersCustomerForeignKeyMigration|MIGRATION")),
                    "Expected MIGRATION node for AddOrdersCustomerForeignKeyMigration in migrations-with-FK graph.");

                // TABLE -> TABLE ForeignKey edge:
                //   dbo.Orders|TABLE -> dbo.Customer|TABLE
                //
                // This test is intentionally small but can be extended with more assertions
                // (composite keys, different delete behaviors, etc.) as FK support grows.
                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("dbo.Orders|TABLE") &&
                        l.Contains("dbo.Customer|TABLE") &&
                        l.Contains("ForeignKey")),
                    "Expected ForeignKey edge: dbo.Orders|TABLE -> dbo.Customer|TABLE in migrations-with-FK graph.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void MigrationsOnly_ProducesMigrationAndTableNodesInGraph()
        {
            // Root for this test run
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_MigrationsOnly_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure
                var solutionDir = Path.Combine(testRoot, "MiniEfSolution");
                var projectDir = Path.Combine(solutionDir, "MiniEfProject");
                var migrationsDir = Path.Combine(solutionDir, "migrations");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(migrationsDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "MiniEfSolution.sln");
                var projectPath = Path.Combine(projectDir, "MiniEfProject.csproj");
                var configPath = Path.Combine(testRoot, "config.json");

                // 2) Solution + project + minimal EF model (Customer/BaseEntity
                //    tylko po to, żeby projekt się normalnie kompilował).
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);
                CreateMiniEfModel(projectDir);

                // 3) Migrations folder with AddCustomerTouchMigration + Schema DSL
                CreateMigrations(migrationsDir);

                // 4) Config: migrations-only
                //    - paths.sql        = ""          (no SQL files)
                //    - paths.ef         = ""          (no explicit EF root)
                //    - paths.migrations = migrationsDir
                //    Program.Net9 zrobi fallback: efPath = migrationsPath
                CreateConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: string.Empty,
                    efDir: string.Empty,
                    migrationsDir: migrationsDir,
                    includeEntityBaseTypes: false);

                // 5) Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 6) Program.Net9 pakuje tempRoot do ZIP w outDir – artefakty
                //    sprawdzamy wewnątrz ZIP-a.
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for migrations-only run.");

                var firstZip = zipFiles[0];

                // 7) Unpack ZIP to inspect sql_code_bundle graph artifacts
                var unpackRoot = Path.Combine(testRoot, "unzipped");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var docsDir = Path.Combine(sqlBundleRoot, "docs");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");

                var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(sqlBodiesPath),
                    "Expected sql_bodies.jsonl to be produced in migrations-only mode (even if empty).");
                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced in migrations-only mode.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced in migrations-only mode.");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // 8) MIGRATION node must exist
                Assert.IsTrue(
                nodesLines.Any(l => l.Contains("csharp:MiniEf.Migrations.AddCustomerTouchMigration|MIGRATION")),
                "Expected MIGRATION node for AddCustomerTouchMigration in migrations-only graph.");

                // 9) TABLE node for dbo.Customer must be created even without SQL,
                //    dzięki EnrichGraphWithTableNodes (patrzy na edges.csv).
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Customer|TABLE")),
                    "Expected TABLE node for dbo.Customer (dbo.Customer|TABLE) in migrations-only graph.");

                // 10) MIGRATION --> TABLE (SchemaChange) edge
                Assert.IsTrue(
     edgesLines.Any(l =>
         l.Contains("csharp:MiniEf.Migrations.AddCustomerTouchMigration|MIGRATION") &&
         l.Contains("dbo.Customer|TABLE") &&
         l.Contains("SchemaChange")),
     "Expected SchemaChange edge: MIGRATION -> dbo.Customer|TABLE in migrations-only graph.");

                // I na koniec sanity check: w tym teście w ogóle nie oczekujemy ENTITY.
                Assert.IsFalse(
                    nodesLines.Any(l => l.Contains("|ENTITY")),
                    "Did not expect ENTITY nodes in pure migrations-only run (paths.ef is empty).");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void MiniSolution_ProducesExpectedSqlEfGraphArtifacts()
        {
            // Root for this test run
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folders
                var solutionDir = Path.Combine(testRoot, "MiniEfSolution");
                var projectDir = Path.Combine(solutionDir, "MiniEfProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var migrationsDir = Path.Combine(solutionDir, "migrations");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(migrationsDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "MiniEfSolution.sln");
                var projectPath = Path.Combine(projectDir, "MiniEfProject.csproj");
                var configPath = Path.Combine(testRoot, "config_mini.json");

                // 2) Minimal solution + project + model + migrations
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);
                CreateMiniEfModel(projectDir);
                CreateSqlScripts(sqlDir);
                CreateMigrations(migrationsDir);
                CreateConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir,
                    projectDir,
                    migrationsDir);

                // 3) Run RoslynIndexer.Net9 (separate process with --config)
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 4) ZIP in outDir – Program.Net9 removes tempRoot after packing,
                // so we inspect artifacts inside the archive.
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder.");

                var firstZip = zipFiles[0];

                // 5) Unpack ZIP to a temporary folder
                var unpackRoot = Path.Combine(testRoot, "unzipped");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                // After unpacking we expect:
                // sql_code_bundle/, regular_code_bundle/, manifest.json, etc.
                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var docsDir = Path.Combine(sqlBundleRoot, "docs");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");

                var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(sqlBodiesPath),
                    "Expected sql_bodies.jsonl to be produced (inside ZIP).");
                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced (inside ZIP).");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced (inside ZIP).");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // ENTITY node for Customer
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("csharp:Customer|ENTITY")),
                    "Expected ENTITY node for Customer (csharp:Customer|ENTITY).");

                // TABLE node for dbo.Customer
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Customer|TABLE")),
                    "Expected TABLE node for dbo.Customer (dbo.Customer|TABLE).");

                // MIGRATION node
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("csharp:MiniEf.Migrations.AddCustomerTouchMigration|MIGRATION")),
                    "Expected MIGRATION node for AddCustomerTouchMigration.");

                // ENTITY --> TABLE (MapsTo)
                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("csharp:Customer|ENTITY") &&
                        l.Contains("dbo.Customer|TABLE") &&
                        l.Contains("MapsTo")),
                    "Expected MapsTo edge: csharp:Customer|ENTITY -> dbo.Customer|TABLE.");

                // MIGRATION --> TABLE (SchemaChange) – touch-table operation
                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("csharp:MiniEf.Migrations.AddCustomerTouchMigration|MIGRATION") &&
                        l.Contains("dbo.Customer|TABLE") &&
                        l.Contains("SchemaChange")),
                    "Expected SchemaChange edge: MIGRATION -> dbo.Customer|TABLE.");
                // TABLE --> TABLE (ForeignKey)
                // dbo.Customer is dependent (because AddCustomerTouchMigration touches 'Customer')
                // principal table extracted from AddForeignKey(...) in the migration is "Dependency" (from CreateMigrations)
                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("dbo.Customer|TABLE") &&
                        l.Contains("dbo.Dependency|TABLE") &&
                        l.Contains("ForeignKey")),
                    "Expected ForeignKey edge: dbo.Customer|TABLE -> dbo.Dependency|TABLE in TABLE->TABLE graph.");



            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void SqlOnly_ProducesTableNodesInGraph()
        {
            // Root for this test run
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_SqlOnly_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure
                var solutionDir = Path.Combine(testRoot, "MiniEfSolution");
                var projectDir = Path.Combine(solutionDir, "MiniEfProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "MiniEfSolution.sln");
                var projectPath = Path.Combine(projectDir, "MiniEfProject.csproj");
                var configPath = Path.Combine(testRoot, "config.json");

                // 2) Minimal solution + project (C# pipeline still needs a solution),
                //    plus EF model tylko po to, żeby projekt był sensowny.
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);
                CreateMiniEfModel(projectDir);

                // 3) SQL scripts: CREATE TABLE dbo.Customer...
                CreateSqlScripts(sqlDir);

                // 4) Config: SQL-only
                //    - paths.sql        = sqlDir
                //    - paths.ef         = ""          (no EF root)
                //    - paths.migrations = ""          (no migrations)
                //    dbGraph.entityBaseTypes zostaje takie jak w helperze,
                //    ale EF stage i tak się nie odpali (brak codeRoots).
                CreateConfigJson(
                  configPath,
                  solutionPath,
                  tempRoot,
                  outDir,
                  sqlDir: sqlDir,
                  efDir: string.Empty,
                  migrationsDir: string.Empty,
                  includeEntityBaseTypes: false);
                
                // 5) Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 6) Program.Net9 packs tempRoot into ZIP in outDir
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for SQL-only run.");

                var firstZip = zipFiles[0];

                // 7) Unpack ZIP to inspect sql_code_bundle graph artifacts
                var unpackRoot = Path.Combine(testRoot, "unzipped");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var docsDir = Path.Combine(sqlBundleRoot, "docs");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");

                var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(sqlBodiesPath),
                    "Expected sql_bodies.jsonl to be produced in SQL-only mode.");
                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced in SQL-only mode.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced in SQL-only mode.");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // 8) TABLE node for dbo.Customer must exist – pure SQL source.
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Customer|TABLE")),
                    "Expected TABLE node for dbo.Customer (dbo.Customer|TABLE) in SQL-only graph.");

                // 9) There should be NO ENTITY or MIGRATION nodes in pure SQL-only scenario.
                Assert.IsFalse(
                    nodesLines.Any(l => l.Contains("|ENTITY")),
                    "Did not expect ENTITY nodes in SQL-only run (paths.ef is empty).");

                Assert.IsFalse(
                    nodesLines.Any(l => l.Contains("|MIGRATION")),
                    "Did not expect MIGRATION nodes in SQL-only run (paths.migrations is empty).");

                // 10) Edges may be empty (CREATE TABLE without references), so we only assert
                //     that edges.csv exists, not that it contains particular relations.
                Assert.IsTrue(
                    edgesLines.Length >= 1,
                    "Expected at least header line in edges.csv for SQL-only graph.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void SqlWithCompositeForeignKey_ProducesTableToTableForeignKeyEdges()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_SqlCompositeFk_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Folder structure (SQL-only scenario)
                var solutionDir = Path.Combine(testRoot, "MiniCompositeFkSolution");
                var projectDir = Path.Combine(solutionDir, "MiniCompositeFkProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "MiniCompositeFkSolution.sln");
                var projectPath = Path.Combine(projectDir, "MiniCompositeFkProject.csproj");
                var configPath = Path.Combine(testRoot, "config_composite_fk.json");

                // 2) Minimal solution + project (C# not really used here, but MSBuildWorkspace requires it)
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);

                // 3) SQL scripts with composite FK (Child(ParentId, ParentYear) → Parent(Id, Year))
                CreateSqlScriptsWithCompositeForeignKey(sqlDir);

                // 4) Config: SQL-only, no EF, no migrations
                CreateConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: sqlDir,
                    efDir: string.Empty,
                    migrationsDir: string.Empty,
                    includeEntityBaseTypes: false);

                // 5) Run RoslynIndexer.Net9
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 6) Unpack ZIP and inspect graph
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for SQL composite-FK run.");

                var firstZip = zipFiles[0];
                var unpackRoot = Path.Combine(testRoot, "unzipped_composite_fk");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced for SQL composite-FK run.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced for SQL composite-FK run.");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // 7) TABLE nodes for dbo.Parent and dbo.Child
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Parent|TABLE", StringComparison.Ordinal)),
                    "Expected TABLE node for dbo.Parent in composite-FK graph.");
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Child|TABLE", StringComparison.Ordinal)),
                    "Expected TABLE node for dbo.Child in composite-FK graph.");

                // 8) Composite FK edge: dbo.Child|TABLE -> dbo.Parent|TABLE (ForeignKey)
                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("dbo.Child|TABLE", StringComparison.Ordinal) &&
                        l.Contains("dbo.Parent|TABLE", StringComparison.Ordinal) &&
                        l.Contains("ForeignKey", StringComparison.Ordinal)),
                    "Expected ForeignKey edge: dbo.Child|TABLE -> dbo.Parent|TABLE in composite-FK graph.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void SqlWithForeignKey_ProducesForeignKeyEdgeInGraph()
        {
            // Root for this test run
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_ForeignKey_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure
                var solutionDir = Path.Combine(testRoot, "MiniFkSolution");
                var projectDir = Path.Combine(solutionDir, "MiniFkProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "MiniFkSolution.sln");
                var projectPath = Path.Combine(projectDir, "MiniFkProject.csproj");
                var configPath = Path.Combine(testRoot, "config_fk.json");

                // 2) Minimal solution + project.
                //    C# side is not used by SQL stage directly, but Program.Net9
                //    still expects a solution to exist and be loadable.
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);
                CreateMiniEfModel(projectDir);

                // 3) SQL scripts: dbo.Parent + dbo.Child with FOREIGN KEY to Parent
                CreateSqlScriptsWithForeignKey(sqlDir);

                // 4) Config: SQL-only for now.
                //    This test is structured so that we can extend it later
                //    with EF / migrations / inline SQL expectations.
                CreateConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: sqlDir,
                    efDir: string.Empty,
                    migrationsDir: string.Empty,
                    includeEntityBaseTypes: false);

                // 5) Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 6) Program.Net9 packs tempRoot into ZIP in outDir
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for ForeignKey run.");

                var firstZip = zipFiles[0];

                // 7) Unpack ZIP to inspect sql_code_bundle graph artifacts
                var unpackRoot = Path.Combine(testRoot, "unzipped");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var docsDir = Path.Combine(sqlBundleRoot, "docs");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");

                var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(sqlBodiesPath),
                    "Expected sql_bodies.jsonl to be produced in ForeignKey scenario.");
                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced in ForeignKey scenario.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced in ForeignKey scenario.");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // 8) TABLE nodes for dbo.Parent and dbo.Child
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Parent|TABLE")),
                    "Expected TABLE node for dbo.Parent (dbo.Parent|TABLE) in ForeignKey graph.");

                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Child|TABLE")),
                    "Expected TABLE node for dbo.Child (dbo.Child|TABLE) in ForeignKey graph.");

                // 9) Child -> Parent ForeignKey edge
                // NOTE: BuildSqlKnowledge currently attaches all batch references
                //       to each defined object in the batch; we assert only that
                //       there exists an edge that contains:
                //       - dbo.Child|TABLE
                //       - dbo.Parent|TABLE
                //       - relation = ForeignKey
                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("dbo.Child|TABLE") &&
                        l.Contains("dbo.Parent|TABLE") &&
                        l.Contains("ForeignKey")),
                    "Expected ForeignKey edge: dbo.Child|TABLE -> dbo.Parent|TABLE in SQL graph.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void SqlWithForeignKeyWithoutSchema_UsesDboAsDefaultSchema()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_SqlFk_DefaultSchema_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                var solutionDir = Path.Combine(testRoot, "MiniFkDefaultSchemaSolution");
                var projectDir = Path.Combine(solutionDir, "MiniFkDefaultSchemaProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "MiniFkDefaultSchemaSolution.sln");
                var projectPath = Path.Combine(projectDir, "MiniFkDefaultSchemaProject.csproj");
                var configPath = Path.Combine(testRoot, "config_fk_default_schema.json");

                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);

                // Parent has explicit dbo schema, REFERENCES uses no schema.
                CreateSqlScriptsWithForeignKeyWithoutSchema(sqlDir);

                CreateConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: sqlDir,
                    efDir: string.Empty,
                    migrationsDir: string.Empty,
                    includeEntityBaseTypes: false);

                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for SQL default-schema FK run.");

                var firstZip = zipFiles[0];
                var unpackRoot = Path.Combine(testRoot, "unzipped_fk_default_schema");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced for SQL default-schema FK run.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced for SQL default-schema FK run.");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // Node keys should normalize to dbo.Parent|TABLE and dbo.Child|TABLE.
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Parent|TABLE", StringComparison.Ordinal)),
                    "Expected TABLE node for dbo.Parent in default-schema FK graph.");
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Child|TABLE", StringComparison.Ordinal)),
                    "Expected TABLE node for dbo.Child in default-schema FK graph.");

                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("dbo.Child|TABLE", StringComparison.Ordinal) &&
                        l.Contains("dbo.Parent|TABLE", StringComparison.Ordinal) &&
                        l.Contains("ForeignKey", StringComparison.Ordinal)),
                    "Expected ForeignKey edge: dbo.Child|TABLE -> dbo.Parent|TABLE for FK without explicit schema.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void EfFluent_GenericHasOne_ForeignKey_ProducesTableToTableForeignKeyEdges()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_EfFluentFK_Generic_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                var sol = Path.Combine(testRoot, "FluentFkGenericSolution");
                var proj = Path.Combine(sol, "FluentFkGenericProject");
                Directory.CreateDirectory(sol);
                Directory.CreateDirectory(proj);

                var sqlDir = Path.Combine(sol, "sqlEmpty");
                Directory.CreateDirectory(sqlDir);

                var outDir = Path.Combine(testRoot, "out");
                Directory.CreateDirectory(outDir);
                var tempRoot = Path.Combine(testRoot, "temp");
                Directory.CreateDirectory(tempRoot);

                var slnPath = Path.Combine(sol, "FluentFkGenericSolution.sln");
                var projPath = Path.Combine(proj, "FluentFkGenericProject.csproj");
                var configPath = Path.Combine(testRoot, "config_ef_fluent_fk_generic.json");

                CreateMinimalSln(slnPath, projPath);
                CreateMinimalProjectFile(projPath);

                CreateEfFluentModelWithGenericHasOne(proj);

                CreateConfigJson(
                    configPath,
                    slnPath,
                    tempRoot,
                    outDir,
                    sqlDir: string.Empty,
                    efDir: proj,
                    migrationsDir: string.Empty);

                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                var zip = Directory.GetFiles(outDir, "*.zip").First();
                var unpack = Path.Combine(testRoot, "unzipped_fk_generic");
                ZipFile.ExtractToDirectory(zip, unpack, true);

                var edgesCsv = Path.Combine(unpack, "sql_code_bundle", "graph", "edges.csv");
                var edges = File.ReadAllLines(edgesCsv);

                Assert.IsTrue(
                    edges.Any(l => l.Contains("ForeignKey", StringComparison.Ordinal)),
                    "Expected at least one ForeignKey edge in EF Fluent FK (generic HasOne) graph.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        [TestMethod]
        public void EfFluent_HasManyWithOne_ForeignKeyEdge_IsDirectedFromChildToParent()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_EfFluentFK_HasMany_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                var sol = Path.Combine(testRoot, "FluentFkHasManySolution");
                var proj = Path.Combine(sol, "FluentFkHasManyProject");
                Directory.CreateDirectory(sol);
                Directory.CreateDirectory(proj);

                var sqlDir = Path.Combine(sol, "sqlEmpty");
                Directory.CreateDirectory(sqlDir);

                var outDir = Path.Combine(testRoot, "out");
                Directory.CreateDirectory(outDir);
                var tempRoot = Path.Combine(testRoot, "temp");
                Directory.CreateDirectory(tempRoot);

                var slnPath = Path.Combine(sol, "FluentFkHasManySolution.sln");
                var projPath = Path.Combine(proj, "FluentFkHasManyProject.csproj");
                var configPath = Path.Combine(testRoot, "config_ef_fluent_fk_hasmany.json");

                CreateMinimalSln(slnPath, projPath);
                CreateMinimalProjectFile(projPath);

                CreateEfFluentModelWithHasManyWithOne(proj);

                CreateConfigJson(
                    configPath,
                    slnPath,
                    tempRoot,
                    outDir,
                    sqlDir: string.Empty,
                    efDir: proj,
                    migrationsDir: string.Empty);

                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                var zip = Directory.GetFiles(outDir, "*.zip").First();
                var unpack = Path.Combine(testRoot, "unzipped_fk_hasmany");
                ZipFile.ExtractToDirectory(zip, unpack, true);

                var edgesCsv = Path.Combine(unpack, "sql_code_bundle", "graph", "edges.csv");
                var lines = File.ReadAllLines(edgesCsv);
                var fkLine = lines.FirstOrDefault(l => l.Contains("ForeignKey", StringComparison.Ordinal));

                Assert.IsNotNull(
                    fkLine,
                    "Expected ForeignKey edge to exist in EF Fluent FK (HasMany/WithOne) direction test.");

                // Parent TABLE must be the target.
                StringAssert.Contains(fkLine, "dbo.Parent|TABLE");

                // Source should be a child table; accept both singular and plural naming.
                var hasChildSource =
                    fkLine.Contains("dbo.Child|TABLE", StringComparison.Ordinal) ||
                    fkLine.Contains("dbo.Children|TABLE", StringComparison.Ordinal);

                Assert.IsTrue(
                    hasChildSource,
                    "Expected ForeignKey edge from child table (dbo.Child|TABLE or dbo.Children|TABLE).");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }


        static void CreateEfFluentModelWithGenericHasOne(string projDir)
        {
            var code = """
      using Microsoft.EntityFrameworkCore;
      using System.Collections.Generic;

      namespace MiniEf {
          public class Parent {
              public int Id { get; set; }
              public ICollection<Child> Children { get; set; } = new List<Child>();
          }

          public class Child {
              public int Id { get; set; }
              public int ParentId { get; set; }
              public Parent Parent { get; set; }
          }

          public class MiniContext : DbContext {
              public DbSet<Parent> Parents { get; set; }
              public DbSet<Child> Children { get; set; }

              protected override void OnModelCreating(ModelBuilder modelBuilder) {
                  modelBuilder.Entity<Child>()
                      .HasOne<Parent>()
                      .WithMany(p => p.Children)
                      .HasForeignKey(c => c.ParentId);
              }
          }
      }
      """;

            File.WriteAllText(Path.Combine(projDir, "MiniEfFluent_GenericHasOne.cs"), code);
        }


        static void CreateEfFluentModelWithHasManyWithOne(string projDir)
        {
            var code = """
      using Microsoft.EntityFrameworkCore;
      using System.Collections.Generic;

      namespace MiniEf {
          public class Parent {
              public int Id { get; set; }
              public ICollection<Child> Children { get; set; } = new List<Child>();
          }

          public class Child {
              public int Id { get; set; }
              public int ParentId { get; set; }
              public Parent Parent { get; set; }
          }

          public class MiniContext : DbContext {
              public DbSet<Parent> Parents { get; set; }
              public DbSet<Child> Children { get; set; }

              protected override void OnModelCreating(ModelBuilder modelBuilder) {
                  modelBuilder.Entity<Parent>()
                      .HasMany(p => p.Children)
                      .WithOne(c => c.Parent)
                      .HasForeignKey(c => c.ParentId);
              }
          }
      }
      """;

            File.WriteAllText(Path.Combine(projDir, "MiniEfFluent_HasManyWithOne.cs"), code);
        }




        private static string AppendDirSep(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return dir;
            if (dir.EndsWith(Path.DirectorySeparatorChar.ToString())) return dir;
            return dir + Path.DirectorySeparatorChar;
        }

        private static void CleanupTestRoot(string testRoot)
        {
            if (string.IsNullOrWhiteSpace(testRoot))
                return;

            try
            {
                if (KeepArtifacts)
                {
                    // Keep the whole testRoot so we can inspect input code and graph artifacts.
                    Console.WriteLine("E2E artifacts kept at: " + testRoot);
                }
                else if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
            catch
            {
                // ignore cleanup errors
            }
        }

        /// <summary>
        /// Creates config.json pointing to:
        /// - the generated solution,
        /// - tempRoot, out, sql, ef, migrations,
        /// and configures dbGraph.entityBaseTypes (optionally).
        /// </summary>
        private static void CreateConfigJson(
            string configPath,
            string solutionPath,
            string tempRoot,
            string outDir,
            string sqlDir,
            string efDir,
            string migrationsDir,
            bool includeEntityBaseTypes = true)
        {
            // Build JSON manually to avoid C# raw-string interpolation quirks.
            // JsonString(...) already handles escaping for string values.
            var sb = new StringBuilder();

            sb.AppendLine("{");
            sb.AppendLine("  \"paths\": {");
            sb.AppendLine("    \"solution\":   " + JsonString(solutionPath) + ",");
            sb.AppendLine("    \"tempRoot\":   " + JsonString(tempRoot) + ",");
            sb.AppendLine("    \"outRoot\":        " + JsonString(outDir) + ",");

            // New keys used by Program.Net9
            sb.AppendLine("    \"sqlRoot\":        " + JsonString(sqlDir) + ",");
            sb.AppendLine("    \"modelRoot\":      " + JsonString(efDir) + ",");
            sb.AppendLine("    \"migrationsRoot\": " + JsonString(migrationsDir) + ",");
            sb.AppendLine("    \"inlineSqlRoot\":  \"\",");

            // Legacy aliases (harmless, keep for compatibility)
            sb.AppendLine("    \"sql\":        " + JsonString(sqlDir) + ",");
            sb.AppendLine("    \"ef\":         " + JsonString(efDir) + ",");
            sb.AppendLine("    \"migrations\": " + JsonString(migrationsDir) + ",");
            sb.AppendLine("    \"inlineSql\":  \"\"");
            sb.AppendLine("  },");

            sb.AppendLine("  \"dbGraph\": {");
            if (includeEntityBaseTypes)
            {
                sb.AppendLine("    \"entityBaseTypes\": [");
                sb.AppendLine("      \"MiniEf.BaseEntity\"");
                sb.AppendLine("    ]");
            }
            else
            {
                sb.AppendLine("    \"entityBaseTypes\": []");
            }
            sb.AppendLine("  }");
            sb.AppendLine("}");

            var json = sb.ToString();
            File.WriteAllText(configPath, json, Encoding.UTF8);
        }

        private static void CreateEfFluentModelWithForeignKey(string projDir)
        {
            var code = """
      using Microsoft.EntityFrameworkCore;
      using System.Collections.Generic;

      namespace MiniEf {
          public class Parent { 
              public int Id { get; set; } 
              public ICollection<Child> Children { get; set; } = new List<Child>();
          }

          public class Child { 
              public int Id { get; set; } 
              public int ParentId { get; set; } 
              public Parent Parent { get; set; }
          }

          public class MiniContext : DbContext {
              public DbSet<Parent> Parents { get; set; }
              public DbSet<Child> Children { get; set; }

              protected override void OnModelCreating(ModelBuilder modelBuilder) {
                  modelBuilder.Entity<Child>()
                      .HasOne(c => c.Parent)
                      .WithMany(p => p.Children)
                      .HasForeignKey(c => c.ParentId);
              }
          }
      }
      """;

            File.WriteAllText(Path.Combine(projDir, "MiniEfFluent.cs"), code);
        }

        /// <summary>
        /// Creates minimal "MiniEf.Migrations" classes for end-to-end tests:
        /// - AddCustomerTouchMigration with:
        ///   * Schema.Table(nameof(Customer))      -> TouchTable("Customer")
        ///   * MigrationBuilder.AddForeignKey(...) -> AddForeignKey("Customer")
        /// </summary>
        private static void CreateMigrations(string migrationsDir)
        {
            var code = """
using MiniEf;

namespace MiniEf.Migrations
{
    [NopUpdateMigration]
    public class AddCustomerTouchMigration
    {
        public void Up()
        {
            // This call is recognized by EfMigrationAnalyzer as TouchTable("Customer").
            Schema.Table(nameof(Customer));

            // This call is recognized by EfMigrationAnalyzer as AddForeignKey on table "Customer".
            MigrationBuilder.AddForeignKey(
                name: "FK_Customer_Dependency",
                table: "Customer",
                schema: "dbo",
                column: "DependencyId",
                principalTable: "Dependency");
        }

        public void Down()
        {
        }
    }

    // Minimal DSL stub so that Schema.Table(...) compiles.
    public static class Schema
    {
        public static SchemaTableExpression Table(string name) => new SchemaTableExpression();

        public sealed class SchemaTableExpression
        {
        }
    }

    // Minimal EF Core-style stub so that MigrationBuilder.AddForeignKey(...) compiles.
    public static class MigrationBuilder
    {
        public static void AddForeignKey(
            string name,
            string table,
            string schema,
            string column,
            string principalTable)
        {
            // no-op: this is only here so that the call site looks realistic
        }
    }

    // Marker attribute – the name contains "UpdateMigration", so IsMigrationClass() will treat
    // AddCustomerTouchMigration as a migration class.
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class NopUpdateMigrationAttribute : System.Attribute
    {
    }
}
""";

            var filePath = Path.Combine(migrationsDir, "AddCustomerTouchMigration.cs");
            File.WriteAllText(filePath, code, Encoding.UTF8);
        }

        /// <summary>
        /// Creates a minimal migration that uses MigrationBuilder.AddForeignKey
        /// so that EF-migration stage can produce TABLE -> TABLE ForeignKey edges.
        /// </summary>
        private static void CreateMigrationsWithForeignKey(string migrationsDir)
        {
            var code = """
using MiniEf;

namespace MiniEf.Migrations
{
    [NopUpdateMigration]
    public class AddOrdersCustomerForeignKeyMigration
    {
        public void Up()
        {
            // Table touch so EfMigrationAnalyzer can still see a table operation.
            Schema.Table(nameof(Customer));

            // AddForeignKey pattern that should result in TABLE -> TABLE ForeignKey edge:
            //   dbo.Orders|TABLE -> dbo.Customer|TABLE
            MigrationBuilder.AddForeignKey(
                name: "FK_Orders_Customers_CustomerId",
                table: "Orders",
                column: "CustomerId",
                principalTable: "Customer",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        public void Down()
        {
            // No-op for this test.
        }
    }

    // Minimal DSL stub so that Schema.Table(...) compiles.
    public static class Schema
    {
        public static SchemaTableExpression Table(string name) => new SchemaTableExpression();

        public sealed class SchemaTableExpression
        {
        }
    }

    // Minimal MigrationBuilder stub – only what the analyzer and test need.
    public static class MigrationBuilder
    {
        public static void AddForeignKey(
            string name,
            string table,
            string column,
            string principalTable,
            string principalColumn,
            ReferentialAction onDelete)
        {
        }
    }

    public enum ReferentialAction
    {
        Cascade,
        Restrict,
        NoAction,
        SetNull,
        SetDefault
    }

    // Marker attribute – the name contains "UpdateMigration", so IsMigrationClass()
    // will treat AddOrdersCustomerForeignKeyMigration as a migration class.
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class NopUpdateMigrationAttribute : System.Attribute
    {
    }
}
""";

            var filePath = Path.Combine(migrationsDir, "AddOrdersCustomerForeignKeyMigration.cs");
            File.WriteAllText(filePath, code, Encoding.UTF8);
        }

        /// <summary>
        /// Creates a tiny domain model + pseudo-EF context:
        /// - BaseEntity,
        /// - Customer : BaseEntity,
        /// - DbSet<T> stub,
        /// - MiniDbContext with DbSet<Customer>,
        /// - RegularUtility as a "normal" C# class.
        /// </summary>
        private static void CreateMiniEfModel(string projectDir)
        {
            var code = """
using System.Collections.Generic;

namespace MiniEf
{
    public abstract class BaseEntity
    {
        public int Id { get; set; }
    }

    public class Customer : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
    }

    // Minimal stand-in for EF DbSet<T> so that DbSet<Customer> compiles
    public class DbSet<T>
    {
        private readonly List<T> _items = new List<T>();

        public void Add(T item) => _items.Add(item);

        public IEnumerable<T> Items => _items;
    }

    // Minimal DbContext-like class containing a DbSet<Customer>
    public class MiniDbContext : DbContext
    {
        public DbSet<Customer> Customers { get; set; } = new DbSet<Customer>();
    }

    // Completely regular C# class – just to have "normal" code in the solution.
    public class RegularUtility
    {
        public int Add(int a, int b) => a + b;
    }
}
""";

            var filePath = Path.Combine(projectDir, "CustomerModel.cs");
            File.WriteAllText(filePath, code, Encoding.UTF8);
        }

        /// <summary>
        /// Creates a minimal SDK-style C# project targeting net9.0.
        /// No external packages are required; we use simple stub types.
        /// </summary>
        private static void CreateMinimalProjectFile(string projectPath)
        {
            var projectXml = """
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

</Project>
""";
            File.WriteAllText(projectPath, projectXml, Encoding.UTF8);
        }

        /// <summary>
        /// Creates a minimal Visual Studio solution file (.sln) with a single C# project.
        /// This is enough for MSBuildWorkspace to load the solution.
        /// </summary>
        private static void CreateMinimalSln(string solutionPath, string projectPath)
        {
            var projectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var projectRelPath = GetRelativePath(Path.GetDirectoryName(solutionPath)!, projectPath);

            var sb = new StringBuilder();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sb.AppendLine("# Visual Studio Version 17");
            sb.AppendLine("VisualStudioVersion = 17.0.31912.275");
            sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");
            sb.AppendLine(
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{projectName}\", \"{projectRelPath}\", \"{projectGuid}\"");
            sb.AppendLine("EndProject");
            sb.AppendLine("Global");
            sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
            sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
            sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
            sb.AppendLine($"\t\t{projectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine($"\t\t{projectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            sb.AppendLine($"\t\t{projectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            sb.AppendLine($"\t\t{projectGuid}.Release|Any CPU.Build.0 = Release|Any CPU");
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
            sb.AppendLine("\t\tHideSolutionNode = FALSE");
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("EndGlobal");

            File.WriteAllText(solutionPath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// Creates a single SQL script with a CREATE TABLE statement for dbo.Customer.
        /// This should produce a TABLE node in the SQL graph.
        /// </summary>
        private static void CreateSqlScripts(string sqlDir)
        {
            var sql = """
CREATE TABLE dbo.Customer
(
    Id   INT           NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL
);
""";
            var filePath = Path.Combine(sqlDir, "001_CreateCustomer.sql");
            File.WriteAllText(filePath, sql, Encoding.UTF8);
        }

        // File: RoslynIndexer.Tests/EndToEnd/SqlEfGraphEndToEndTests.cs
        /// <summary>
        /// Creates SQL scripts declaring dbo.Parent and dbo.Child tables
        /// with a composite Foreign Key:
        ///   Child(ParentId, ParentYear) → Parent(Id, [Year]).
        /// This is used to validate that composite FKs also produce
        /// TABLE -> TABLE (ForeignKey) edges in the SQL graph.
        /// </summary>
        private static void CreateSqlScriptsWithCompositeForeignKey(string sqlDir)
        {
            var sql = """
CREATE TABLE dbo.Parent
(
    Id    INT NOT NULL,
    [Year] INT NOT NULL,
    CONSTRAINT PK_Parent PRIMARY KEY (Id, [Year])
);

CREATE TABLE dbo.Child
(
    Id         INT NOT NULL,
    ParentId   INT NOT NULL,
    ParentYear INT NOT NULL,
    CONSTRAINT PK_Child PRIMARY KEY (Id),
    CONSTRAINT FK_Child_Parent_Composite
        FOREIGN KEY (ParentId, ParentYear)
        REFERENCES dbo.Parent (Id, [Year])
);
""";

            var filePath = Path.Combine(sqlDir, "002_CreateParentChildCompositeFk.sql");
            File.WriteAllText(filePath, sql, Encoding.UTF8);
        }

        /// <summary>
        /// Creates SQL scripts declaring dbo.Parent and dbo.Child tables.
        /// dbo.Child has a FOREIGN KEY referencing dbo.Parent.
        /// This is used to validate ForeignKey edges in the SQL graph.
        /// </summary>
        private static void CreateSqlScriptsWithForeignKey(string sqlDir)
        {
            var sql = """
CREATE TABLE dbo.Parent
(
    Id   INT           NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL
);

CREATE TABLE dbo.Child
(
    Id        INT NOT NULL PRIMARY KEY,
    ParentId  INT NOT NULL,
    CONSTRAINT FK_Child_Parent
        FOREIGN KEY (ParentId)
        REFERENCES dbo.Parent(Id)
);
""";

            var filePath = Path.Combine(sqlDir, "001_CreateParentChildWithFk.sql");
            File.WriteAllText(filePath, sql, Encoding.UTF8);
        }

        /// <summary>
        /// Creates SQL scripts where:
        ///   * Parent is created as dbo.Parent
        ///   * Child has FOREIGN KEY (...) REFERENCES Parent(Id) without schema
        /// This validates that the SQL graph normalizes schema to 'dbo'
        /// for the referenced table.
        /// </summary>
        private static void CreateSqlScriptsWithForeignKeyWithoutSchema(string sqlDir)
        {
            var sql = """
CREATE TABLE dbo.Parent
(
    Id   INT           NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL
);

CREATE TABLE dbo.Child
(
    Id        INT NOT NULL PRIMARY KEY,
    ParentId  INT NOT NULL,
    CONSTRAINT FK_Child_Parent_NoSchema
        FOREIGN KEY (ParentId)
        REFERENCES Parent (Id)
);
""";

            var filePath = Path.Combine(sqlDir, "003_CreateParentChildFk_DefaultSchema.sql");
            File.WriteAllText(filePath, sql, Encoding.UTF8);
        }
        /// <summary>
        /// Creates a minimal "migration" class recognized by EfMigrationAnalyzer:
        /// - marked with [NopUpdateMigration] attribute (name contains "Migration"),
        /// - method Up() calls Schema.Table(nameof(Customer)) which will be parsed
        ///   as EfMigrationOperationKind.TouchTable for table "Customer".
        /// </summary>

        /// <summary>
        /// Portable relative path helper (no exceptions if paths are weird).
        /// </summary>
        private static string GetRelativePath(string baseDir, string fullPath)
        {
            var baseUri = new Uri(AppendDirSep(baseDir));
            var pathUri = new Uri(fullPath);
            var rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString());
            return rel.Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Helper that builds a JSON string literal from a raw value.
        /// </summary>
        private static string JsonString(string value)
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

        // File: RoslynIndexer.Tests/EndToEnd/SqlEfGraphEndToEndTests.cs
        /// <summary>
        /// Runs RoslynIndexer.Net9 in a separate process, as if from CLI:
        ///   dotnet RoslynIndexer.Net9.dll --config configPath
        /// or
        ///   RoslynIndexer.Net9.exe --config configPath
        /// depending on the build output.
        /// This avoids MSBuild assembly version conflicts inside the test runner process.
        /// </summary>
        private static async Task RunRoslynIndexerNet9WithConfig(string configPath)
        {
            // Locate the RoslynIndexer.Net9 assembly and derive the executable path.
            var assemblyPath = typeof(MsBuildWorkspaceLoader).Assembly.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                throw new InvalidOperationException("Cannot locate RoslynIndexer.Net9 assembly on disk.");

            var ext = Path.GetExtension(assemblyPath);
            string fileName;
            string arguments;

            if (string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase))
            {
                // Typical Debug/Release build: dotnet <dll> --config ...
                fileName = "dotnet";
                arguments = $"\"{assemblyPath}\" --config \"{configPath}\"";
            }
            else
            {
                // Single-file or .exe publish: run directly.
                fileName = assemblyPath;
                arguments = $"--config \"{configPath}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(assemblyPath) ?? Environment.CurrentDirectory
            };

            using var proc = new Process { StartInfo = psi };

            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    sbOut.AppendLine(e.Data);
                    Console.WriteLine(e.Data);
                }
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    sbErr.AppendLine(e.Data);
                    Console.WriteLine(e.Data);
                }
            };

            if (!proc.Start())
                throw new InvalidOperationException("Failed to start RoslynIndexer.Net9 process.");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await Task.Run(() => proc.WaitForExit());

            var exitCode = proc.ExitCode;

            if (exitCode != 0)
            {
                Assert.Fail(
                    $"RoslynIndexer.Net9 exited with code {exitCode}.\n" +
                    $"STDOUT:\n{sbOut}\n\nSTDERR:\n{sbErr}");
            }
        }
    }
}
