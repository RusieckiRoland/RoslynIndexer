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
    ///    - regular C# project,
    ///    - one inline SQL query in a C# method,
    ///    - a SQL folder with CREATE TABLE dbo.Customer (for TABLE node),
    /// 2) Writes config.json pointing to those paths, including paths.inlineSql,
    /// 3) Executes RoslynIndexer.Net9 Program.Main("--config", configPath),
    /// 4) Verifies that the SQL/EF graph contains:
    ///    - TABLE node for dbo.Customer (from .sql),
    ///    - METHOD node for the C# method that uses inline SQL,
    ///    - ReadsFrom edge: METHOD -> dbo.Customer|TABLE.
    ///
    /// NOTE: this is a SPEC for future inline SQL support – it will initially fail
    ///       until inline SQL extraction and graph wiring are implemented.
    /// </summary>
    [TestClass]
    public class InlineSqlEndToEndTests
    {
        // When set to "1", end-to-end tests will keep their temp directories
        // so you can inspect both the generated code and the produced graph artifacts.
        private static readonly bool KeepArtifacts =
            string.Equals(
                Environment.GetEnvironmentVariable("RI_KEEP_E2E"),
                "1",
                StringComparison.Ordinal);

        [TestMethod]
        public void InlineSql_ProducesMethodNodeAndReadsFromEdgeInGraph()
        {
            // Root for this test run
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_InlineSql_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure
                var solutionDir = Path.Combine(testRoot, "InlineSqlSolution");
                var projectDir = Path.Combine(solutionDir, "InlineSqlProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
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

                // 3) C# model with inline SQL + simple runner stub
                CreateInlineSqlModel(projectDir);

                // 4) SQL scripts: CREATE TABLE dbo.Customer...
                CreateSqlScripts(sqlDir);

                // 5) Config: SQL + inline SQL
                //    - paths.sql        = sqlDir
                //    - paths.ef         = ""              (no EF root)
                //    - paths.migrations = ""              (no migrations)
                //    - paths.inlineSql  = projectDir      (C# project with inline SQL)
                //
                // Graph-wise, the spec expects:
                //   - TABLE node: dbo.Customer|TABLE      (from .sql script)
                //   - METHOD node: csharp:InlineSqlSample.RawSql.LoadCustomers|METHOD
                //   - ReadsFrom edge: METHOD -> dbo.Customer|TABLE
                CreateInlineSqlConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: sqlDir,
                    inlineSqlDir: projectDir);

                // 6) Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 7) Program.Net9 packs tempRoot into ZIP in outDir.
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for inline-SQL run.");

                var firstZip = zipFiles[0];

                // 8) Unpack ZIP to inspect SQL graph artifacts.
                // For now we reuse the legacy sql_code_bundle layout (nodes/edges.csv).
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
                    "Expected sql_bodies.jsonl to be produced (inside ZIP) for inline-SQL scenario.");
                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced (inside ZIP) for inline-SQL scenario.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced (inside ZIP) for inline-SQL scenario.");

                var nodesLines = File.ReadAllLines(nodesCsvPath);
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                // 9) TABLE node for dbo.Customer must exist – from the .sql script.
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Customer|TABLE")),
                    "Expected TABLE node for dbo.Customer (dbo.Customer|TABLE) in inline-SQL graph.");

                // 10) METHOD node for the C# method that uses inline SQL:
                //     namespace  = InlineSqlSample
                //     class      = RawSql
                //     method     = LoadCustomers
                //
                // Spec for key:
                //   csharp:InlineSqlSample.RawSql.LoadCustomers|METHOD
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("csharp:InlineSqlSample.RawSql.LoadCustomers|METHOD")),
                    "Expected METHOD node for InlineSqlSample.RawSql.LoadCustomers representing raw SQL usage.");

                // 11) METHOD --> TABLE (ReadsFrom) edge:
                //     ReadsFrom edge from InlineSqlSample.RawSql.LoadCustomers to dbo.Customer|TABLE.
                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("csharp:InlineSqlSample.RawSql.LoadCustomers|METHOD") &&
                        l.Contains("dbo.Customer|TABLE") &&
                        l.Contains("ReadsFrom")),
                    "Expected ReadsFrom edge: METHOD (InlineSqlSample.RawSql.LoadCustomers) -> dbo.Customer|TABLE.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        /// <summary>
        /// End-to-end check that mixes:
        /// - plain text (should NOT be treated as SQL),
        /// - short/weak "SQL-like" strings (should NOT be treated as SQL),
        /// - proper SELECT/INSERT snippets (heuristic LooksLikeSql = true),
        /// - built-in hot methods (SqlQuery/ExecuteSql/FromSql) with both SQL and non-SQL text.
        ///
        /// The goal is to have a fast regression test that tells us if the inline-SQL
        /// classifier started taking too much or too little.
        /// </summary>
        [TestMethod]
        public void InlineSql_MixedPatterns_UsesHeuristicsAndHotMethodsAsExpected()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_InlineSqlMixed_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure
                var solutionDir = Path.Combine(testRoot, "MixedInlineSqlSolution");
                var projectDir = Path.Combine(solutionDir, "MixedInlineSqlProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "MixedInlineSqlSolution.sln");
                var projectPath = Path.Combine(projectDir, "MixedInlineSqlProject.csproj");
                var configPath = Path.Combine(testRoot, "config.json");

                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);

                // C# code containing different inline-SQL patterns
                CreateMixedInlineSqlModel(projectDir);

                // Simple SQL script so LegacySqlIndexer has something to index
                CreateSqlScripts(sqlDir);

                // Inline-SQL enabled via inlineSqlRoot
                CreateInlineSqlConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: sqlDir,
                    inlineSqlDir: projectDir);

                // Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // Read sql_bodies.jsonl from the produced ZIP
                var bodiesText = ReadSqlBodiesFromFirstZip(outDir);

                // Methods that MUST be treated as SQL (detected):
                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlMixedSample.MixedInlineSql.Heuristic_SelectFrom");

                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlMixedSample.MixedInlineSql.Heuristic_InsertInto");

                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlMixedSample.MixedInlineSql.BuiltIn_SqlQuery_WithRealSql");

                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlMixedSample.MixedInlineSql.BuiltIn_SqlQuery_WithNonSql");

                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlMixedSample.MixedInlineSql.BuiltIn_ExecuteSql_WithNonSql");

                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlMixedSample.MixedInlineSql.BuiltIn_FromSql_WithNonSql");

                // Methods that MUST stay non-SQL (ignored by the classifier):
                AssertDoesNotContainSqlForMethod(
                    bodiesText,
                    "InlineSqlMixedSample.MixedInlineSql.PlainText_NoSql");

                AssertDoesNotContainSqlForMethod(
                    bodiesText,
                    "InlineSqlMixedSample.MixedInlineSql.Short_Select_NoStructure");

                AssertDoesNotContainSqlForMethod(
                    bodiesText,
                    "InlineSqlMixedSample.MixedInlineSql.FakeSqlWords_NoStructure");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        /// <summary>
        /// End-to-end test for built-in hot methods:
        ///   SqlQuery, ExecuteSql, FromSql
        ///
        /// For these methods ANY payload (even obvious non-SQL text) must be treated as SQL,
        /// while a control method with the same payload (but non-hot name) must NOT be treated as SQL.
        /// </summary>
        [TestMethod]
        public void InlineSql_BuiltInHotMethods_TreatAnyContentAsSql()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_InlineSql_HotMethods_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                var solutionDir = Path.Combine(testRoot, "HotMethodsSolution");
                var projectDir = Path.Combine(solutionDir, "HotMethodsProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "HotMethodsSolution.sln");
                var projectPath = Path.Combine(projectDir, "HotMethodsProject.csproj");
                var configPath = Path.Combine(testRoot, "config.json");

                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);
                CreateBuiltInHotMethodsModel(projectDir);
                CreateSqlScripts(sqlDir);

                CreateInlineSqlConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: sqlDir,
                    inlineSqlDir: projectDir);

                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                var bodiesText = ReadSqlBodiesFromFirstZip(outDir);

                // All three built-in hot methods must be classified as SQL,
                // even though the payload is intentionally non-SQL.
                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlHotMethodsSample.HotMethods.BuiltIn_SqlQuery_WithNonSql");

                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlHotMethodsSample.HotMethods.BuiltIn_ExecuteSql_WithNonSql");

                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlHotMethodsSample.HotMethods.BuiltIn_FromSql_WithNonSql");

                // Control method (same payload, non-hot method name) must NOT be treated as SQL.
                AssertDoesNotContainSqlForMethod(
                    bodiesText,
                    "InlineSqlHotMethodsSample.HotMethods.Control_PlainLog_WithNonSql");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        /// <summary>
        /// End-to-end test for inlineSql.extraHotMethods:
        ///
        /// - When ExecuteRawSql / RunAdHocQuery are listed in extraHotMethods,
        ///   their payload must be treated as SQL (even if not SQL-looking).
        /// - When extraHotMethods is empty, the same calls must NOT be treated as SQL.
        ///
        /// A plain Log method must never be treated as SQL in either configuration.
        /// </summary>
        [TestMethod]
        public void InlineSql_ExtraHotMethods_RespectConfigToggle()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_InlineSql_ExtraHot_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                var solutionDir = Path.Combine(testRoot, "ExtraHotSolution");
                var projectDir = Path.Combine(solutionDir, "ExtraHotProject");
                var sqlDir = Path.Combine(solutionDir, "sql");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);

                var solutionPath = Path.Combine(solutionDir, "ExtraHotSolution.sln");
                var projectPath = Path.Combine(projectDir, "ExtraHotProject.csproj");

                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);

                CreateExtraHotInlineSqlModel(projectDir);
                CreateSqlScripts(sqlDir);

                // --- Run 1: extraHotMethods configured ---

                var tempRootWith = Path.Combine(testRoot, "temp_with");
                var outDirWith = Path.Combine(testRoot, "out_with");
                Directory.CreateDirectory(tempRootWith);
                Directory.CreateDirectory(outDirWith);

                var configWith = Path.Combine(testRoot, "config_with.json");

                CreateInlineSqlConfigJsonWithInlineSettings(
                    configWith,
                    solutionPath,
                    tempRootWith,
                    outDirWith,
                    sqlDir,
                    projectDir,
                    extraHotMethods: new[] { "ExecuteRawSql", "RunAdHocQuery" });

                RunRoslynIndexerNet9WithConfig(configWith).GetAwaiter().GetResult();
                var bodiesWith = ReadSqlBodiesFromFirstZip(outDirWith);

                // --- Run 2: extraHotMethods = [] (disabled) ---

                var tempRootWithout = Path.Combine(testRoot, "temp_without");
                var outDirWithout = Path.Combine(testRoot, "out_without");
                Directory.CreateDirectory(tempRootWithout);
                Directory.CreateDirectory(outDirWithout);

                var configWithout = Path.Combine(testRoot, "config_without.json");

                CreateInlineSqlConfigJsonWithInlineSettings(
                    configWithout,
                    solutionPath,
                    tempRootWithout,
                    outDirWithout,
                    sqlDir,
                    projectDir,
                    extraHotMethods: Array.Empty<string>());

                RunRoslynIndexerNet9WithConfig(configWithout).GetAwaiter().GetResult();
                var bodiesWithout = ReadSqlBodiesFromFirstZip(outDirWithout);

                const string executeRawSqlMethod =
                    "InlineSqlExtraHotSample.ExtraHotUser.UsesExtraHot_ExecuteRawSql";
                const string runAdHocQueryMethod =
                    "InlineSqlExtraHotSample.ExtraHotUser.UsesExtraHot_RunAdHocQuery";
                const string plainLogMethod =
                    "InlineSqlExtraHotSample.ExtraHotUser.UsesPlainLog_WithNonSql";

                // With extraHotMethods configured: both extra-hot calls must be treated as SQL.
                AssertContainsSqlForMethod(bodiesWith, executeRawSqlMethod);
                AssertContainsSqlForMethod(bodiesWith, runAdHocQueryMethod);

                // Plain log must NEVER be classified as SQL.
                AssertDoesNotContainSqlForMethod(bodiesWith, plainLogMethod);
                AssertDoesNotContainSqlForMethod(bodiesWithout, plainLogMethod);

                // Without extraHotMethods: the same calls must NOT be treated as SQL.
                AssertDoesNotContainSqlForMethod(bodiesWithout, executeRawSqlMethod);
                AssertDoesNotContainSqlForMethod(bodiesWithout, runAdHocQueryMethod);
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }


        /// <summary>
        /// Creates config.json for inline-SQL tests and additionally emits:
        /// 
        ///   "inlineSql": {
        ///     "extraHotMethods": [ ... ]
        ///   }
        ///
        /// so we can verify inlineSql.extraHotMethods end-to-end.
        /// </summary>
        private static void CreateInlineSqlConfigJsonWithInlineSettings(
            string configPath,
            string solutionPath,
            string tempRoot,
            string outDir,
            string sqlDir,
            string inlineSqlDir,
            string[] extraHotMethods)
        {
            var sb = new StringBuilder();

            sb.AppendLine("{");
            sb.AppendLine("  \"paths\": {");
            sb.AppendLine("    \"solution\":   " + JsonString(solutionPath) + ",");
            sb.AppendLine("    \"modelRoot\":      \"\",");
            sb.AppendLine("    \"sqlRoot\":        " + JsonString(sqlDir) + ",");
            sb.AppendLine("    \"inlineSqlRoot\":  " + JsonString(inlineSqlDir) + ",");
            sb.AppendLine("    \"migrationsRoot\": \"\",");
            sb.AppendLine("    \"outRoot\":        " + JsonString(outDir) + ",");
            sb.AppendLine("    \"tempRoot\":   " + JsonString(tempRoot));
            sb.AppendLine("  },");
            sb.AppendLine("  \"dbGraph\": {");
            sb.AppendLine("    \"entityBaseTypes\": []");
            sb.AppendLine("  },");

            sb.AppendLine("  \"inlineSql\": {");
            sb.Append("    \"extraHotMethods\": [");

            if (extraHotMethods != null && extraHotMethods.Length > 0)
            {
                sb.Append(string.Join(
                    ", ",
                    extraHotMethods.Select(m => JsonString(m))));
            }

            sb.AppendLine("]");
            sb.AppendLine("  }");
            sb.AppendLine("}");

            File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
        }





        /// <summary>
        /// Creates a minimal Visual Studio solution file (.sln) with a single C# project.
        /// This is enough for MSBuildWorkspace to load the solution.
        /// </summary>
        private static void CreateMinimalSln(string solutionPath, string projectPath)
        {
            var projectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectName = Path.GetFileNameWithoutExtension(projectPath);

            // projectDirName = np. "InlineSqlProject", "MixedInlineSqlProject", "HotMethodsProject", itd.
            var projectDirName = Path.GetFileName(Path.GetDirectoryName(projectPath));
            var projectRelPath = Path.Combine(projectDirName ?? string.Empty, Path.GetFileName(projectPath));

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
        /// Creates a minimal SDK-style C# project targeting net9.0.
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
        /// Creates a tiny C# model with:
        /// - a SQL-like table consumer,
        /// - one method containing inline SQL referring to dbo.Customer,
        /// - a fake runner to "execute" the SQL (no real DB dependency).
        /// </summary>
        private static void CreateInlineSqlModel(string projectDir)
        {
            var code = """
namespace InlineSqlSample
{
    // Fake runner – simulates an API that executes raw SQL.
    public static class FakeRunner
    {
        public static void Execute(string sql)
        {
            // no-op: this is only here so that the call site looks realistic
        }
    }

    public static class RawSql
    {
        public static void LoadCustomers()
        {
            // Inline SQL we want the indexer to detect and wire into the graph.
            const string sql = @"
SELECT c.Id, c.Name
FROM dbo.Customer c
WHERE c.Id > 0;
";

            // Typical pattern: pass SQL to some executor.
            FakeRunner.Execute(sql);
        }
    }
}
""";

            var filePath = Path.Combine(projectDir, "InlineSqlSample.cs");
            File.WriteAllText(filePath, code, Encoding.UTF8);
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

        /// <summary>
        /// Creates config.json pointing to:
        /// - the generated solution,
        /// - tempRoot, out, sql,
        /// - and configures paths.inlineSql / paths.inlineSqlRoot = inlineSqlDir.
        /// dbGraph.entityBaseTypes is kept empty – this test focuses on inline SQL, not EF.
        /// </summary>
        private static void CreateInlineSqlConfigJson(
            string configPath,
            string solutionPath,
            string tempRoot,
            string outDir,
            string sqlDir,
            string inlineSqlDir)
        {
            var sb = new StringBuilder();

            sb.AppendLine("{");
            sb.AppendLine("  \"paths\": {");
            sb.AppendLine("    \"solution\":   " + JsonString(solutionPath) + ",");
            sb.AppendLine("    \"modelRoot\":      \"\",");
           
            sb.AppendLine("    \"sqlRoot\":        " + JsonString(sqlDir) + ",");
            sb.AppendLine("    \"inlineSqlRoot\":  " + JsonString(inlineSqlDir) + ",");
            sb.AppendLine("    \"migrationsRoot\": \"\",");
            sb.AppendLine("    \"outRoot\":        " + JsonString(outDir) + ",");
            sb.AppendLine("    \"tempRoot\":   " + JsonString(tempRoot));
            sb.AppendLine("  },");
            sb.AppendLine("  \"dbGraph\": {");
            sb.AppendLine("    \"entityBaseTypes\": []");
            sb.AppendLine("  }");
            sb.AppendLine("}");

            var json = sb.ToString();
            File.WriteAllText(configPath, json, Encoding.UTF8);
        }


        /// <summary>
        /// Runs RoslynIndexer.Net9 in a separate process, as if from CLI:
        ///   dotnet RoslynIndexer.Net9.dll --config configPath
        /// or
        ///   RoslynIndexer.Net9.exe --config configPath
        /// depending on the build output.
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

        /// <summary>
        /// Creates a C# file with a mix of:
        /// - plain non-SQL strings,
        /// - weak/short SQL-like strings,
        /// - proper SELECT/INSERT strings,
        /// - built-in hot methods with both SQL and non-SQL payloads.
        /// </summary>
        private static void CreateMixedInlineSqlModel(string projectDir)
        {
            var code = """
namespace InlineSqlMixedSample
{
    public static class FakeDb
    {
        public static void SqlQuery(string sql)
        {
        }

        public static void ExecuteSql(string sql)
        {
        }

        public static void FromSql(string sql)
        {
        }

        public static void Log(string text)
        {
        }
    }

    public static class MixedInlineSql
    {
        public static void PlainText_NoSql()
        {
            const string text = "This is plain text without any SQL keywords at all.";
            FakeDb.Log(text);
        }

        public static void Short_Select_NoStructure()
        {
            const string text = "SELECT 1";
            FakeDb.Log(text);
        }

        public static void FakeSqlWords_NoStructure()
        {
            const string text =
                "We will UPDATE documentation soon, but this is not structured SQL at all.";
            FakeDb.Log(text);
        }

        public static void Heuristic_SelectFrom()
        {
            const string text =
                "SELECT c.Id, c.Name FROM dbo.Customer c WHERE c.Id > 0;";
            FakeDb.Log(text);
        }

        public static void Heuristic_InsertInto()
        {
            const string text =
                "INSERT INTO dbo.Customer(Id, Name) VALUES(1, 'A');";
            FakeDb.Log(text);
        }

        public static void BuiltIn_SqlQuery_WithRealSql()
        {
            const string sql =
                "SELECT c.Id FROM dbo.Customer c WHERE c.Id > 10;";
            FakeDb.SqlQuery(sql);
        }

        public static void BuiltIn_SqlQuery_WithNonSql()
        {
            const string sql =
                "This is clearly not SQL text, but SqlQuery is a hot method so it must be treated as SQL.";
            FakeDb.SqlQuery(sql);
        }

        public static void BuiltIn_ExecuteSql_WithNonSql()
        {
            const string sql =
                "Another non-SQL payload that must still be treated as SQL because ExecuteSql is hot.";
            FakeDb.ExecuteSql(sql);
        }

        public static void BuiltIn_FromSql_WithNonSql()
        {
            const string sql =
                "Yet another non-SQL payload that must still be classified as SQL due to FromSql.";
            FakeDb.FromSql(sql);
        }
    }
}
""";

            var filePath = Path.Combine(projectDir, "MixedInlineSqlSample.cs");
            File.WriteAllText(filePath, code, Encoding.UTF8);
        }

        /// <summary>
        /// Creates a tiny C# model focused purely on built-in hot methods:
        /// SqlQuery / ExecuteSql / FromSql.
        /// All payloads are intentionally non-SQL.
        /// </summary>
        private static void CreateBuiltInHotMethodsModel(string projectDir)
        {
            var code = """
namespace InlineSqlHotMethodsSample
{
    public static class FakeDb
    {
        public static void SqlQuery(string sql)
        {
        }

        public static void ExecuteSql(string sql)
        {
        }

        public static void FromSql(string sql)
        {
        }

        public static void Log(string text)
        {
        }
    }

    public static class HotMethods
    {
        public static void BuiltIn_SqlQuery_WithNonSql()
        {
            const string payload = "Non-SQL payload for SqlQuery hot method.";
            FakeDb.SqlQuery(payload);
        }

        public static void BuiltIn_ExecuteSql_WithNonSql()
        {
            const string payload = "Non-SQL payload for ExecuteSql hot method.";
            FakeDb.ExecuteSql(payload);
        }

        public static void BuiltIn_FromSql_WithNonSql()
        {
            const string payload = "Non-SQL payload for FromSql hot method.";
            FakeDb.FromSql(payload);
        }

        public static void Control_PlainLog_WithNonSql()
        {
            const string payload = "Non-SQL payload for plain Log (must not be treated as SQL).";
            FakeDb.Log(payload);
        }
    }
}
""";

            var filePath = Path.Combine(projectDir, "HotMethodsSample.cs");
            File.WriteAllText(filePath, code, Encoding.UTF8);
        }

        /// <summary>
        /// Creates a C# model used to test inlineSql.extraHotMethods.
        /// CustomExecutors.ExecuteRawSql / RunAdHocQuery are the "extra hot" methods.
        /// </summary>
        private static void CreateExtraHotInlineSqlModel(string projectDir)
        {
            var code = """
namespace InlineSqlExtraHotSample
{
    public static class CustomExecutors
    {
        public static void ExecuteRawSql(string sql)
        {
        }

        public static void RunAdHocQuery(string sql)
        {
        }

        public static void Log(string text)
        {
        }
    }

    public static class ExtraHotUser
    {
        public static void UsesExtraHot_ExecuteRawSql()
        {
            const string payload =
                "Non-SQL payload routed through ExecuteRawSql, must be treated as SQL only when configured as extra-hot.";
            CustomExecutors.ExecuteRawSql(payload);
        }

        public static void UsesExtraHot_RunAdHocQuery()
        {
            const string payload =
                "Non-SQL payload routed through RunAdHocQuery, must be treated as SQL only when configured as extra-hot.";
            CustomExecutors.RunAdHocQuery(payload);
        }

        public static void UsesPlainLog_WithNonSql()
        {
            const string payload =
                "Non-SQL payload passed to plain Log, must never be treated as SQL.";
            CustomExecutors.Log(payload);
        }
    }
}
""";

            var filePath = Path.Combine(projectDir, "ExtraHotInlineSqlSample.cs");
            File.WriteAllText(filePath, code, Encoding.UTF8);
        }

        /// <summary>
        /// Unpacks the first ZIP from outDir and returns the concatenated contents of sql_bodies.jsonl.
        /// </summary>
        private static string ReadSqlBodiesFromFirstZip(string outDir)
        {
            var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
            Assert.IsTrue(zipFiles.Count >= 1,
                "Expected at least one ZIP archive in the 'out' folder for inline-SQL run.");

            var firstZip = zipFiles[0];

            var unpackRoot = Path.Combine(outDir, "unzipped");
            if (Directory.Exists(unpackRoot))
            {
                Directory.Delete(unpackRoot, recursive: true);
            }

            Directory.CreateDirectory(unpackRoot);
            ZipFile.ExtractToDirectory(firstZip, unpackRoot);

            var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
            var docsDir = Path.Combine(sqlBundleRoot, "docs");
            var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");

            Assert.IsTrue(File.Exists(sqlBodiesPath),
                "Expected sql_bodies.jsonl to be produced inside ZIP for inline-SQL run.");

            return File.ReadAllText(sqlBodiesPath);
        }

        /// <summary>
        /// Asserts that sql_bodies.jsonl contains at least one artifact associated with the given method.
        /// We search by fully-qualified method name string.
        /// </summary>
        private static void AssertContainsSqlForMethod(string bodiesText, string methodFullName)
        {
            Assert.IsTrue(
                bodiesText.Contains(methodFullName, StringComparison.Ordinal),
                $"Expected sql_bodies.jsonl to contain methodFullName '{methodFullName}' (inline SQL should be detected).");
        }

        /// <summary>
        /// Asserts that sql_bodies.jsonl does NOT contain any artifact associated with the given method.
        /// </summary>
        private static void AssertDoesNotContainSqlForMethod(string bodiesText, string methodFullName)
        {
            Assert.IsFalse(
                bodiesText.Contains(methodFullName, StringComparison.Ordinal),
                $"Did not expect sql_bodies.jsonl to contain methodFullName '{methodFullName}' for non-SQL scenario.");
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
    }
}
