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
    /// End-to-end tests for .slnx support.
    ///
    /// These scenarios construct a tiny on-disk solution that uses a .slnx file
    /// instead of classic .sln, then execute the real RoslynIndexer.Net9 CLI:
    ///
    ///   dotnet RoslynIndexer.Net9.dll --config config.json
    ///
    /// and verify that:
    ///   - the run succeeds,
    ///   - ZIP is produced in outRoot,
    ///   - sql_code_bundle/sql_bodies.jsonl exists inside the ZIP.
    ///
    /// NOTE: Initially this may fail if MsBuildWorkspaceLoader does not yet support .slnx;
    ///       in that case this class acts as a SPEC for future .slnx workspace support.
    /// </summary>
    [TestClass]
    public class SlnxEndToEndTests
    {
        private static readonly bool KeepArtifacts =
            string.Equals(
                Environment.GetEnvironmentVariable("RI_KEEP_E2E"),
                "1",
                StringComparison.Ordinal);

        /// <summary>
        /// Happy-path .slnx scenario:
        /// - one C# project,
        /// - one SQL script with CREATE TABLE dbo.Customer,
        /// - config.paths.solution points to Sample.slnx.
        ///
        /// Expectation:
        /// - CLI exits with code 0,
        /// - a ZIP is produced in outRoot,
        /// - sql_code_bundle/sql_bodies.jsonl exists inside the ZIP.
        /// </summary>
        [TestMethod]
        public void Slnx_HappyPath_ProducesSqlBodiesAndCodeBundle()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_Slnx_Happy_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Folder layout
                var solutionDir = Path.Combine(testRoot, "SlnxSolution");
                var projectDir = Path.Combine(solutionDir, "SlnxProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "SlnxSolution.slnx");
                var projectPath = Path.Combine(projectDir, "SlnxProject.csproj");
                var configPath = Path.Combine(testRoot, "config_slnx_happy.json");

                // 2) Minimal .slnx + project
                CreateMinimalSlnx(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);

                // 3) Simple SQL script so LegacySqlIndexer has something to index
                CreateSqlScripts(sqlDir);

                // 4) Config: .slnx + sqlRoot + modelRoot
                CreateBasicSlnxConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir,
                    modelRoot: projectDir);

                // 5) Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 6) Inspect produced ZIP
                var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
                Assert.IsTrue(
                    zipFiles.Count >= 1,
                    "Expected at least one ZIP archive in the 'out' folder for .slnx run.");

                var firstZip = zipFiles[0];

                var unpackRoot = Path.Combine(testRoot, "unzipped_slnx");
                if (Directory.Exists(unpackRoot))
                    Directory.Delete(unpackRoot, recursive: true);

                Directory.CreateDirectory(unpackRoot);
                ZipFile.ExtractToDirectory(firstZip, unpackRoot);

                var sqlBundleRoot = Path.Combine(unpackRoot, "sql_code_bundle");
                var docsDir = Path.Combine(sqlBundleRoot, "docs");
                var graphDir = Path.Combine(sqlBundleRoot, "graph");

                var sqlBodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");
                var nodesCsvPath = Path.Combine(graphDir, "nodes.csv");
                var edgesCsvPath = Path.Combine(graphDir, "edges.csv");

                Assert.IsTrue(
                    File.Exists(sqlBodiesPath),
                    "Expected sql_bodies.jsonl to be produced for .slnx scenario.");
                Assert.IsTrue(
                    File.Exists(nodesCsvPath),
                    "Expected nodes.csv to be produced for .slnx scenario.");
                Assert.IsTrue(
                    File.Exists(edgesCsvPath),
                    "Expected edges.csv to be produced for .slnx scenario.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        /// <summary>
        /// EF-only .slnx scenario:
        /// - paths.sqlRoot is empty,
        /// - paths.modelRoot points to the C# project,
        /// - solution is still a .slnx file.
        ///
        /// Expectation:
        /// - run succeeds,
        /// - sql_bodies.jsonl is produced from EF-only mode.
        /// </summary>
        [TestMethod]
        public void Slnx_EfOnlyMode_ProducesSqlBodies()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_Slnx_EfOnly_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                var solutionDir = Path.Combine(testRoot, "SlnxEfSolution");
                var projectDir = Path.Combine(solutionDir, "EfProject");
                var sqlDir = Path.Combine(solutionDir, "sql"); // optional; can be empty
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "SlnxEfSolution.slnx");
                var projectPath = Path.Combine(projectDir, "EfProject.csproj");
                var configPath = Path.Combine(testRoot, "config_slnx_efonly.json");

                CreateMinimalSlnx(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);
                CreateEfOnlyProjectSources(projectDir);   // ⬅️ DODANE

                // For EF-only mode we do not strictly need SQL scripts,
                // but having one keeps the layout closer to other tests.
                CreateSqlScripts(sqlDir);

                CreateEfOnlySlnxConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    modelRoot: projectDir);

                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                var bodiesText = ReadSqlBodiesFromFirstZip(outDir);
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(bodiesText),
                    "Expected sql_bodies.jsonl to contain at least one line in EF-only .slnx scenario.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        /// <summary>
        /// Adds minimal EF migration source so that EF-only mode produces at least one SQL body.
        /// We don't need real EF packages – analyzer only parses syntax.
        /// </summary>
        private static void CreateEfOnlyProjectSources(string projectDir)
        {
            var migrationsDir = Path.Combine(projectDir, "Migrations");
            Directory.CreateDirectory(migrationsDir);

            var migrationCs = """
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfProject.Migrations
{
    public partial class Init : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Minimal SQL body for the analyzer to pick up.
            migrationBuilder.Sql(
                "CREATE TABLE dbo.EfCustomer (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE dbo.EfCustomer;");
        }
    }
}
""";

            var migrationPath = Path.Combine(migrationsDir, "20250101000000_Init.cs");
            File.WriteAllText(migrationPath, migrationCs, Encoding.UTF8);
        }


        // ============================================================
        // Helpers (local to this test class)
        // ============================================================

        /// <summary>
        /// Creates a minimal .slnx file with a single C# project entry.
        /// The exact schema matches our SlnxSolutionFileReader: we only need
        /// an element with a Path attribute that points to a supported project.
        /// </summary>
        private static void CreateMinimalSlnx(string solutionPath, string projectPath)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var projectDirName = Path.GetFileName(Path.GetDirectoryName(projectPath));
            var projectRelPath = Path.Combine(projectDirName ?? string.Empty, Path.GetFileName(projectPath));

            var sb = new StringBuilder();
            sb.AppendLine("<Solution>");
            sb.AppendLine("  <Projects>");
            sb.AppendLine(
                $"    <Project Name=\"{projectName}\" Path=\"{projectRelPath}\" />");
            sb.AppendLine("  </Projects>");
            sb.AppendLine("</Solution>");

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
        /// Basic config.json for .slnx happy-path scenario:
        /// - paths.solution   = .slnx file,
        /// - paths.sqlRoot    = sqlDir,
        /// - paths.modelRoot  = modelRoot,
        /// - paths.inlineSqlRoot/migrationsRoot empty.
        /// </summary>
        private static void CreateBasicSlnxConfigJson(
            string configPath,
            string solutionPath,
            string tempRoot,
            string outDir,
            string sqlDir,
            string modelRoot)
        {
            var sb = new StringBuilder();

            sb.AppendLine("{");
            sb.AppendLine("  \"paths\": {");
            sb.AppendLine("    \"solution\":   " + JsonString(solutionPath) + ",");
            sb.AppendLine("    \"modelRoot\":      " + JsonString(modelRoot) + ",");
            sb.AppendLine("    \"sqlRoot\":        " + JsonString(sqlDir) + ",");
            sb.AppendLine("    \"inlineSqlRoot\":  \"\",");
            sb.AppendLine("    \"migrationsRoot\": \"\",");
            sb.AppendLine("    \"outRoot\":        " + JsonString(outDir) + ",");
            sb.AppendLine("    \"tempRoot\":   " + JsonString(tempRoot));
            sb.AppendLine("  },");
            sb.AppendLine("  \"dbGraph\": {");
            sb.AppendLine("    \"entityBaseTypes\": []");
            sb.AppendLine("  }");
            sb.AppendLine("}");

            File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// Config.json for EF-only .slnx scenario:
        /// - no sqlRoot,
        /// - modelRoot points to the EF/C# project.
        /// </summary>
        private static void CreateEfOnlySlnxConfigJson(
            string configPath,
            string solutionPath,
            string tempRoot,
            string outDir,
            string modelRoot)
        {
            var sb = new StringBuilder();

            sb.AppendLine("{");
            sb.AppendLine("  \"paths\": {");
            sb.AppendLine("    \"solution\":   " + JsonString(solutionPath) + ",");
            sb.AppendLine("    \"modelRoot\":      " + JsonString(modelRoot) + ",");
            sb.AppendLine("    \"sqlRoot\":        \"\",");
            sb.AppendLine("    \"inlineSqlRoot\":  \"\",");
            sb.AppendLine("    \"migrationsRoot\": \"\",");
            sb.AppendLine("    \"outRoot\":        " + JsonString(outDir) + ",");
            sb.AppendLine("    \"tempRoot\":   " + JsonString(tempRoot));
            sb.AppendLine("  },");
            sb.AppendLine("  \"dbGraph\": {");
            sb.AppendLine("    \"entityBaseTypes\": []");
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
        /// Copied from existing E2E tests for consistency.
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
                fileName = "dotnet";
                arguments = $"\"{assemblyPath}\" --config \"{configPath}\"";
            }
            else
            {
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
        /// Unpacks the first ZIP from outDir and returns the concatenated contents of sql_bodies.jsonl.
        /// </summary>
        private static string ReadSqlBodiesFromFirstZip(string outDir)
        {
            var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
            Assert.IsTrue(zipFiles.Count >= 1,
                "Expected at least one ZIP archive in the 'out' folder for .slnx run.");

            var firstZip = zipFiles[0];

            var unpackRoot = Path.Combine(outDir, "unzipped_slnx_sql_bodies");
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
                "Expected sql_bodies.jsonl to be produced inside ZIP for .slnx run.");

            return File.ReadAllText(sqlBodiesPath);
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

        private static void CleanupTestRoot(string testRoot)
        {
            if (string.IsNullOrWhiteSpace(testRoot))
                return;

            try
            {
                if (KeepArtifacts)
                {
                    Console.WriteLine("E2E .slnx artifacts kept at: " + testRoot);
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
