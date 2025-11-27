using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Net9.Adapters; // MsBuildWorkspaceLoader to locate RoslynIndexer.Net9 assembly
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RoslynIndexer.Tests.EndToEnd
{
    /// <summary>
    /// End-to-end regression test that stress-tests InlineSqlScanner + LooksLikeSql
    /// on a large, mixed sample of string literals.
    ///
    /// The test constructs a tiny solution on disk with:
    ///   - one C# project containing 201 string literals:
    ///       * 101 "SQL-looking" strings that MUST be classified as inline SQL
    ///         (w tym zapytania z JOIN, które referują DWIE tabele),
    ///       * 100 non-SQL strings that MUST NOT be classified as inline SQL;
    ///   - one SQL script providing a simple dbo.Customer table for the SQL stage.
    ///
    /// The indexer is then executed via the real RoslynIndexer.Net9 CLI
    /// (dotnet RoslynIndexer.Net9.dll --config config.json) and the produced
    /// sql_bodies.jsonl is inspected. We count how many entries have
    ///   kind == "InlineSQL".
    ///
    /// Because JOIN patterns produce two SQL references per string, the expected
    /// InlineSQL artifact count is EXACTLY 111 (not 101). Any tightening/loosening
    /// of LooksLikeSql, or changes in how SQL references are collected, that alter
    /// which strings/tables/procs are recognized will change this total and cause
    /// the test to fail.
    /// </summary>
    [TestClass]
    public class LooksLikeSqlEndToEndTests
    {
        // When set to "1", end-to-end tests will keep their temp directories
        // so you can inspect both the generated code and produced graph artifacts.
        private static readonly bool KeepArtifacts =
            string.Equals(
                Environment.GetEnvironmentVariable("RI_KEEP_E2E"),
                "1",
                StringComparison.Ordinal);

        [TestMethod]
        public void LooksLikeSql_Stress_ProducesExpectedInlineSqlArtifactCount()
        {
            // Root for this test run
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_LooksLikeSql_Stress_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure (similar to other E2E tests)
                var solutionDir = Path.Combine(testRoot, "LooksLikeSqlSolution");
                var projectDir = Path.Combine(solutionDir, "LooksLikeSqlProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "LooksLikeSqlSolution.sln");
                var projectPath = Path.Combine(projectDir, "LooksLikeSqlProject.csproj");
                var configPath = Path.Combine(testRoot, "config.json");

                // 2) Minimal solution + project
                CreateMinimalSln(solutionPath, projectPath, projectDirName: "LooksLikeSqlProject");
                CreateMinimalProjectFile(projectPath);

                // 3) C# model with 201 cases:
                //    - 101 strings that MUST be treated as SQL (LooksLikeSql == true),
                //    - 100 strings that MUST NOT be treated as SQL (LooksLikeSql == false).
                CreateLooksLikeSqlStressModel(projectDir);

                // 4) Simple SQL script so LegacySqlIndexer has something to index
                CreateSqlScripts(sqlDir);

                // 5) Config: SQL + inline SQL
                CreateInlineSqlConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: sqlDir,
                    inlineSqlDir: projectDir);

                // 6) Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 7) Read sql_bodies.jsonl from the produced ZIP
                var bodiesText = ReadSqlBodiesFromFirstZip(outDir);

                // 8) Count InlineSQL artifacts only
                var lines = bodiesText
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var inlineSqlLines = lines
                    .Where(l => l.Contains("\"kind\":\"InlineSQL\"", StringComparison.Ordinal))
                    .ToList();

                const int expectedInlineSqlCount = 111;

                Assert.AreEqual(
                    expectedInlineSqlCount,
                    inlineSqlLines.Count,
                    $"Expected exactly {expectedInlineSqlCount} InlineSQL artifact(s) in sql_bodies.jsonl " +
                    "for the LooksLikeSql stress sample (heuristics only). A different count means " +
                    "LooksLikeSql started taking too much or too little.");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        /// <summary>
        /// Creates a C# file containing 201 test strings:
        ///   - 111 "SQL-looking" strings that MUST satisfy LooksLikeSql(value) == true,
        ///   - 100 non-SQL strings that MUST satisfy LooksLikeSql(value) == false.
        ///
        /// All strings are declared as local const string values inside a single
        /// StressCases.All() method. The InlineSqlScanner walks all string
        /// literals and uses LooksLikeSql to decide which ones become InlineSQL
        /// artifacts.
        /// </summary>
        private static void CreateLooksLikeSqlStressModel(string projectDir)
        {
            var sb = new StringBuilder();

            sb.AppendLine("namespace InlineSqlLooksLikeSqlStressSample");
            sb.AppendLine("{");
            sb.AppendLine("    public static class FakeDb");
            sb.AppendLine("    {");
            sb.AppendLine("        public static void Log(string text)");
            sb.AppendLine("        {");
            sb.AppendLine("            // no-op: this keeps call sites realistic for the scanner");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public static class StressCases");
            sb.AppendLine("    {");
            sb.AppendLine("        public static void All()");
            sb.AppendLine("        {");
            sb.AppendLine("            // === POSITIVE CASES (LooksLikeSql == true) ===");

            // We deliberately construct a variety of shapes:
            //   - SELECT ... FROM ... WHERE ...
            //   - INSERT INTO ...
            //   - UPDATE ... WHERE ...
            //   - DELETE FROM ... WHERE ...
            //   - SELECT ... JOIN ...
            //   - SELECT ... FROM [dbo].[Table]
            //   - EXEC ... with a trailing "FROM" in a comment to satisfy structure token.

            var positiveTemplates = new Func<int, string>[]
            {
                i => $"SELECT Id FROM dbo.Table{i:D3} WHERE Id > 0", // classic SELECT-FROM-WHERE
                i => $"SELECT Id, Name FROM dbo.Customers{i:D3} WHERE Name LIKE 'A%'", // LIKE
                i => $"INSERT INTO dbo.Table{i:D3}(Id, Name) VALUES({i}, 'Name{i:D3}')", // INSERT INTO
                i => $"UPDATE dbo.Table{i:D3} SET Name = 'Updated{i:D3}' WHERE Id = {i}", // UPDATE ... WHERE
                i => $"DELETE FROM dbo.Table{i:D3} WHERE IsDeleted = 0 AND Id = {i}", // DELETE FROM ... WHERE
                i => $"SELECT o.Id FROM dbo.Orders{i:D3} o JOIN dbo.Customers{i:D3} c ON c.Id = o.CustomerId", // JOIN
                i => $"SELECT t.Id, t.Name FROM [dbo].[Table{i:D3}] t WHERE t.Name LIKE 'B%'", // bracketed identifiers [dbo].[Table]
                i => $"SELECT COUNT(*) FROM dbo.Logs{i:D3} WHERE CreatedOn >= '2024-01-01'", // aggregate + WHERE
                i => $"SELECT TOP 10 * FROM dbo.History{i:D3} WHERE EventType = 'X'", // TOP
                i => $"EXEC dbo.Proc{i:D3} -- FROM dbo.Table{i:D3} (for LooksLikeSql structure)" // EXEC + FROM in comment
            };

            int positiveCount = 0;
            for (int i = 1; i <= 101; i++)
            {
                var tpl = positiveTemplates[(i - 1) % positiveTemplates.Length];
                var value = tpl(i);
                var constName = $"Sql_Pos_{i:D3}";

                sb.Append("            const string ");
                sb.Append(constName);
                sb.Append(" = \"");
                sb.Append(value.Replace("\\", "\\\\").Replace("\"", "\\\""));
                sb.AppendLine("\";");
                sb.AppendLine($"            FakeDb.Log({constName});");
                sb.AppendLine();

                positiveCount++;
            }

            sb.AppendLine("            // === NEGATIVE CASES (LooksLikeSql == false) ===");

            var negativeTemplates = new Func<int, string>[]
            {
                // Too short (length < 12) – even though it looks a bit like SQL.
                i => "SELECT 1",

                // Has verb UPDATE but no FROM/INTO/WHERE/JOIN/TABLE tokens.
                i => "We will UPDATE documentation soon", // conversational text

                // Has FROM but no SQL verb at all.
                i => "From start to finish this is pure prose.",

                // Has TABLE but no SQL verb.
                i => "This table contains user settings only.",

                // Has JOIN but no SQL verb.
                i => "Join us for a party tomorrow evening.",

                // Has INSERT word but no structural token.
                i => "Please insert card and follow the instructions.",

                // Has DELETE word but no structural token.
                i => "Press delete key to remove a character.",

                // Has MERGE word but intentionally avoids " INTO ".
                i => "We plan to merge branches later today in git.",

                // Has EXEC word but no structural token.
                i => "The exec summary of the meeting is attached.",

                // Completely neutral, no SQL words at all.
                i => $"This is just a plain log message number {i}."
            };

            int negativeCount = 0;
            for (int i = 1; i <= 100; i++)
            {
                var tpl = negativeTemplates[(i - 1) % negativeTemplates.Length];
                var value = tpl(i);
                var constName = $"Sql_Neg_{i:D3}";

                sb.Append("            const string ");
                sb.Append(constName);
                sb.Append(" = \"");
                sb.Append(value.Replace("\\", "\\\\").Replace("\"", "\\\""));
                sb.AppendLine("\";");
                sb.AppendLine($"            FakeDb.Log({constName});");
                sb.AppendLine();

                negativeCount++;
            }

            sb.AppendLine("            // sanity check at generation time (helps while editing the test)");
            sb.AppendLine("            // expected: 101 positive, 100 negative cases declared above.");
            sb.AppendLine($"            // positiveCount = {positiveCount}, negativeCount = {negativeCount};");

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            var filePath = Path.Combine(projectDir, "LooksLikeSqlStressSample.cs");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// Creates a minimal Visual Studio solution file (.sln) with a single C# project.
        /// This is enough for MSBuildWorkspace to load the solution.
        /// </summary>
        private static void CreateMinimalSln(string solutionPath, string projectPath, string projectDirName)
        {
            var projectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var projectRelPath = Path.Combine(projectDirName, Path.GetFileName(projectPath));

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
        /// - and configures paths.inlineSqlRoot = inlineSqlDir.
        /// dbGraph.entityBaseTypes is kept empty – this test focuses purely on inline SQL.
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
        /// Unpacks the first ZIP from outDir and returns the concatenated contents of sql_bodies.jsonl.
        /// </summary>
        private static string ReadSqlBodiesFromFirstZip(string outDir)
        {
            var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
            Assert.IsTrue(zipFiles.Count >= 1,
                "Expected at least one ZIP archive in the 'out' folder for LooksLikeSql stress run.");

            var firstZip = zipFiles[0];

            var unpackRoot = Path.Combine(outDir, "unzipped_LooksLikeSqlStress");
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
                "Expected sql_bodies.jsonl to be produced inside ZIP for LooksLikeSql stress run.");

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
