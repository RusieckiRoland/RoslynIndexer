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
    /// End-to-end test that creates a small solution with:
    ///
    /// - several SQL scripts (TABLE / VIEW / PROC),
    /// - several C# methods with inline SQL (heuristics + built-in hot methods + extraHotMethods),
    ///
    /// then runs RoslynIndexer.Net9 and verifies that:
    /// - SQL bodies are written to docs/bodies/*.sql and sql_bodies.jsonl,
    /// - InlineSQL entries are present in sql_bodies.jsonl (via methodFullName),
    /// - graph artifacts (nodes.csv, edges.csv, graph.json) contain expected nodes and edges.
    ///
    /// This test is based purely on the current code:
    ///  - SqlEfGraphIndexer.BuildSqlKnowledge for .sql bodies and graph,
    ///  - InlineSqlScanner + AppendInlineSqlEdgesAndNodes_FromArtifacts +
    ///    AppendInlineSqlBodiesToJsonl for inline SQL.
    /// </summary>
    [TestClass]
    public class SqlBodiesEndToEndTests
    {
        // When set to "1", end-to-end tests will keep their temp directories
        // so you can inspect both the generated code and the produced graph artifacts.
        private static readonly bool KeepArtifacts =
            string.Equals(
                Environment.GetEnvironmentVariable("RI_KEEP_E2E"),
                "1",
                StringComparison.Ordinal);

        [TestMethod]
        public void SqlAndInlineSql_ProduceBodiesDocsAndGraphArtifacts()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_Bodies_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure
                var solutionDir = Path.Combine(testRoot, "BodiesSolution");
                var projectDir = Path.Combine(solutionDir, "BodiesProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "BodiesSolution.sln");
                var projectPath = Path.Combine(projectDir, "BodiesProject.csproj");
                var configPath = Path.Combine(testRoot, "config.json");

                // 2) Minimal solution + project
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);

                // 3) C# model with several inline SQL methods (heuristics + hot + extraHot)
                CreateInlineBodiesModel(projectDir);

                // 4) Several SQL scripts: TABLE / VIEW / PROC referencing each other
                CreateSqlBodiesScripts(sqlDir);

                // 5) Config: SQL + inline SQL (+ extraHotMethods for inlineSql)
                CreateInlineBodiesConfigJson(
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
                    "Expected at least one ZIP archive in the 'out' folder for bodies end-to-end run.");

                var firstZip = zipFiles[0];

                // 8) Unpack ZIP to inspect SQL graph + docs artifacts.
                var unpackRoot = Path.Combine(testRoot, "unzipped");
                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var docsDir = Path.Combine(sqlBundleRoot, "docs");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");
                var bodiesDir = Path.Combine(docsDir, "bodies");
                var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");
                var graphJsonPath = Path.Combine(graphDir, "graph.json");

                Assert.IsTrue(Directory.Exists(docsDir),
                    "Expected docs directory to be produced inside sql_code_bundle.");
                Assert.IsTrue(Directory.Exists(bodiesDir),
                    "Expected docs/bodies directory to be produced inside sql_code_bundle.");
                Assert.IsTrue(File.Exists(sqlBodiesPath),
                    "Expected docs/sql_bodies.jsonl to be produced.");

                Assert.IsTrue(File.Exists(nodesCsvPath),
                    "Expected graph/nodes.csv to be produced.");
                Assert.IsTrue(File.Exists(edgesCsvPath),
                    "Expected graph/edges.csv to be produced.");
                Assert.IsTrue(File.Exists(graphJsonPath),
                    "Expected graph/graph.json to be produced.");

                // 9) SQL bodies: at least 5 .sql body files in docs/bodies.
                var bodyFiles = Directory.EnumerateFiles(bodiesDir, "*.sql").ToList();
                Assert.IsTrue(bodyFiles.Count >= 5,
                    $"Expected at least 5 SQL body files in docs/bodies, found {bodyFiles.Count}.");

                // Specific bodies for known objects from CreateSqlBodiesScripts
                Assert.IsTrue(
                    File.Exists(Path.Combine(bodiesDir, "dbo.Customer.TABLE.sql")),
                    "Expected body file for dbo.Customer TABLE in docs/bodies.");
                Assert.IsTrue(
                    File.Exists(Path.Combine(bodiesDir, "dbo.Order.TABLE.sql")),
                    "Expected body file for dbo.Order TABLE in docs/bodies.");
                Assert.IsTrue(
                    File.Exists(Path.Combine(bodiesDir, "dbo.Product.TABLE.sql")),
                    "Expected body file for dbo.Product TABLE in docs/bodies.");
                Assert.IsTrue(
                    File.Exists(Path.Combine(bodiesDir, "dbo.CustomerOrders.VIEW.sql")),
                    "Expected body file for dbo.CustomerOrders VIEW in docs/bodies.");
                Assert.IsTrue(
                    File.Exists(Path.Combine(bodiesDir, "dbo.GetCustomerOrders.PROC.sql")),
                    "Expected body file for dbo.GetCustomerOrders PROC in docs/bodies.");

                // 10) sql_bodies.jsonl: mix of plain SQL objects and InlineSQL entries
                var bodiesText = File.ReadAllText(sqlBodiesPath);

                // a) Entries for SQL objects (created by BuildSqlKnowledge)
                Assert.IsTrue(
                    bodiesText.Contains("dbo.Customer|TABLE", StringComparison.Ordinal),
                    "Expected sql_bodies.jsonl to contain entry for dbo.Customer|TABLE.");
                Assert.IsTrue(
                    bodiesText.Contains("dbo.Order|TABLE", StringComparison.Ordinal),
                    "Expected sql_bodies.jsonl to contain entry for dbo.Order|TABLE.");
                Assert.IsTrue(
                    bodiesText.Contains("dbo.Product|TABLE", StringComparison.Ordinal),
                    "Expected sql_bodies.jsonl to contain entry for dbo.Product|TABLE.");
                Assert.IsTrue(
                    bodiesText.Contains("dbo.CustomerOrders|VIEW", StringComparison.Ordinal),
                    "Expected sql_bodies.jsonl to contain entry for dbo.CustomerOrders|VIEW.");
                Assert.IsTrue(
                    bodiesText.Contains("dbo.GetCustomerOrders|PROC", StringComparison.Ordinal),
                    "Expected sql_bodies.jsonl to contain entry for dbo.GetCustomerOrders|PROC.");

                // b) Inline SQL entries (created by AppendInlineSqlBodiesToJsonl)
                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlBodiesSample.SqlConsumers.Heuristic_SelectFromCustomer");
                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlBodiesSample.SqlConsumers.Heuristic_JoinCustomerOrders");
                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlBodiesSample.SqlConsumers.Heuristic_SelectFromProduct");
                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlBodiesSample.SqlConsumers.BuiltIn_SqlQuery_UsesView");
                AssertContainsSqlForMethod(
                    bodiesText,
                    "InlineSqlBodiesSample.SqlConsumers.ExtraHot_ExecuteRawSql_UsesProc");

                // 11) nodes.csv: TABLE nodes and METHOD nodes
                var nodesLines = File.ReadAllLines(nodesCsvPath);

                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Customer|TABLE", StringComparison.Ordinal)),
                    "Expected TABLE node for dbo.Customer (dbo.Customer|TABLE) in nodes.csv.");
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Order|TABLE", StringComparison.Ordinal)),
                    "Expected TABLE node for dbo.Order (dbo.Order|TABLE) in nodes.csv.");
                Assert.IsTrue(
                    nodesLines.Any(l => l.Contains("dbo.Product|TABLE", StringComparison.Ordinal)),
                    "Expected TABLE node for dbo.Product (dbo.Product|TABLE) in nodes.csv.");

                Assert.IsTrue(
                    nodesLines.Any(l =>
                        l.Contains("csharp:InlineSqlBodiesSample.SqlConsumers.Heuristic_SelectFromCustomer|METHOD",
                            StringComparison.Ordinal)),
                    "Expected METHOD node for Heuristic_SelectFromCustomer in nodes.csv.");
                Assert.IsTrue(
                    nodesLines.Any(l =>
                        l.Contains("csharp:InlineSqlBodiesSample.SqlConsumers.BuiltIn_SqlQuery_UsesView|METHOD",
                            StringComparison.Ordinal)),
                    "Expected METHOD node for BuiltIn_SqlQuery_UsesView in nodes.csv.");
                Assert.IsTrue(
                    nodesLines.Any(l =>
                        l.Contains("csharp:InlineSqlBodiesSample.SqlConsumers.ExtraHot_ExecuteRawSql_UsesProc|METHOD",
                            StringComparison.Ordinal)),
                    "Expected METHOD node for ExtraHot_ExecuteRawSql_UsesProc in nodes.csv.");

                // 12) edges.csv: at least some ReadsFrom edges from our METHOD nodes
                var edgesLines = File.ReadAllLines(edgesCsvPath);

                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("csharp:InlineSqlBodiesSample.SqlConsumers.Heuristic_SelectFromCustomer|METHOD",
                            StringComparison.Ordinal) &&
                        l.Contains("ReadsFrom", StringComparison.Ordinal)),
                    "Expected at least one ReadsFrom edge from Heuristic_SelectFromCustomer METHOD node.");

                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("csharp:InlineSqlBodiesSample.SqlConsumers.BuiltIn_SqlQuery_UsesView|METHOD",
                            StringComparison.Ordinal) &&
                        l.Contains("ReadsFrom", StringComparison.Ordinal)),
                    "Expected at least one ReadsFrom edge from BuiltIn_SqlQuery_UsesView METHOD node.");

                Assert.IsTrue(
                    edgesLines.Any(l =>
                        l.Contains("csharp:InlineSqlBodiesSample.SqlConsumers.ExtraHot_ExecuteRawSql_UsesProc|METHOD",
                            StringComparison.Ordinal) &&
                        l.Contains("ReadsFrom", StringComparison.Ordinal)),
                    "Expected at least one ReadsFrom edge from ExtraHot_ExecuteRawSql_UsesProc METHOD node.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        /// <summary>
        /// Creates a minimal Visual Studio solution file (.sln) with a single C# project.
        /// This is enough for MSBuildWorkspace to load the solution.
        /// </summary>
        private static void CreateMinimalSln(string solutionPath, string projectPath)
        {
            var projectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectName = Path.GetFileNameWithoutExtension(projectPath);

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
        /// Creates a C# file with several inline SQL consumers:
        /// - 3 heuristic-based (LooksLikeSql = true),
        /// - 1 built-in hot method (SqlQuery),
        /// - 1 extra-hot method (ExecuteRawSql).
        /// All queries reference SQL objects created in CreateSqlBodiesScripts.
        /// </summary>
        private static void CreateInlineBodiesModel(string projectDir)
        {
            var code = """
namespace InlineSqlBodiesSample
{
    public static class DbExecutor
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

    public static class SqlConsumers
    {
        public static void Heuristic_SelectFromCustomer()
        {
            const string sql =
                "SELECT Id, Name FROM dbo.Customer WHERE Id > 10;";
            DbExecutor.Log(sql);
        }

        public static void Heuristic_JoinCustomerOrders()
        {
            const string sql =
                "SELECT c.Id, o.Id AS OrderId FROM dbo.Customer c JOIN dbo.[Order] o ON o.CustomerId = c.Id;";
            DbExecutor.Log(sql);
        }

        public static void Heuristic_SelectFromProduct()
        {
            const string sql =
                "SELECT p.Id, p.Name FROM dbo.Product p WHERE p.Id >= 1;";
            DbExecutor.Log(sql);
        }

        public static void BuiltIn_SqlQuery_UsesView()
        {
            const string sql =
                "SELECT * FROM dbo.CustomerOrders;";
            DbExecutor.SqlQuery(sql);
        }

        public static void ExtraHot_ExecuteRawSql_UsesProc()
        {
            const string sql =
                "EXEC dbo.GetCustomerOrders;";
            DbExecutor.ExecuteRawSql(sql);
        }
    }
}
""";

            var filePath = Path.Combine(projectDir, "InlineSqlBodiesSample.cs");
            File.WriteAllText(filePath, code, Encoding.UTF8);
        }

        /// <summary>
        /// Creates several SQL scripts:
        /// - 3 TABLEs: dbo.Customer, dbo.Order, dbo.Product,
        /// - 1 VIEW:  dbo.CustomerOrders (joining Customer and Order),
        /// - 1 PROC:  dbo.GetCustomerOrders (selects from the view).
        /// </summary>
        private static void CreateSqlBodiesScripts(string sqlDir)
        {
            var customerSql = """
CREATE TABLE dbo.Customer
(
    Id   INT           NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL
);
""";
            File.WriteAllText(
                Path.Combine(sqlDir, "001_CreateCustomer.sql"),
                customerSql,
                Encoding.UTF8);

            var orderSql = """
CREATE TABLE dbo.[Order]
(
    Id         INT NOT NULL PRIMARY KEY,
    CustomerId INT NOT NULL
);
""";
            File.WriteAllText(
                Path.Combine(sqlDir, "002_CreateOrder.sql"),
                orderSql,
                Encoding.UTF8);

            var productSql = """
CREATE TABLE dbo.Product
(
    Id   INT           NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL
);
""";
            File.WriteAllText(
                Path.Combine(sqlDir, "003_CreateProduct.sql"),
                productSql,
                Encoding.UTF8);

            var viewSql = """
CREATE VIEW dbo.CustomerOrders
AS
SELECT
    c.Id       AS CustomerId,
    c.Name     AS CustomerName,
    o.Id       AS OrderId
FROM dbo.Customer c
JOIN dbo.[Order] o ON o.CustomerId = c.Id;
""";
            File.WriteAllText(
                Path.Combine(sqlDir, "010_ViewCustomerOrders.sql"),
                viewSql,
                Encoding.UTF8);

            var procSql = """
CREATE PROCEDURE dbo.GetCustomerOrders
AS
BEGIN
    SELECT *
    FROM dbo.CustomerOrders;
END;
""";
            File.WriteAllText(
                Path.Combine(sqlDir, "020_ProcGetCustomerOrders.sql"),
                procSql,
                Encoding.UTF8);
        }

        /// <summary>
        /// Creates config.json for the bodies test:
        /// - paths.sqlRoot       = sqlDir
        /// - paths.inlineSqlRoot = inlineSqlDir
        /// - paths.migrationsRoot = "" (no migrations)
        /// - inlineSql.extraHotMethods = [ "ExecuteRawSql", "RunAdHocQuery" ]
        /// dbGraph.entityBaseTypes remains empty (focus is on SQL + inline SQL).
        /// </summary>
        private static void CreateInlineBodiesConfigJson(
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
            sb.AppendLine("  },");
            sb.AppendLine("  \"inlineSql\": {");
            sb.AppendLine("    \"extraHotMethods\": [");
            sb.AppendLine("      " + JsonString("ExecuteRawSql") + ",");
            sb.AppendLine("      " + JsonString("RunAdHocQuery"));
            sb.AppendLine("    ]");
            sb.AppendLine("  }");
            sb.AppendLine("}");

            File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
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
