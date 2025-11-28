using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Net9.Adapters; // MsBuildWorkspaceLoader to locate RoslynIndexer.Net9 assembly
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RoslynIndexer.Net9.Tests.EndToEnd
{
    /// <summary>
    /// End-to-end spec for EF-style migrations:
    /// 1) Creates a tiny solution with:
    ///    - a C# project containing:
    ///        * a stub Migration base type,
    ///        * a stub MigrationBuilder with EF-like APIs,
    ///        * multiple migration classes with Up() methods calling:
    ///              CreateTable, DropTable,
    ///              AddColumn, DropColumn,
    ///              RenameColumn,
    ///              AddForeignKey, DropForeignKey,
    ///              Sql("...") (raw SQL),
    ///    - a simple SQL folder so the indexer has sqlRoot (not the focus here),
    /// 2) Writes config.json with:
    ///        paths.solution       = this solution,
    ///        paths.sqlRoot        = sql folder,
    ///        paths.migrationsRoot = C# project with migrations,
    ///        dbGraph.entityBaseTypes = [],
    /// 3) Runs RoslynIndexer.Net9 Program with that config,
    /// 4) Unpacks the resulting ZIP and inspects docs/sql_bodies.jsonl.
    ///
    /// Expectations (spec):
    /// - sql_bodies.jsonl contains MIGRATION entries for all migration classes;
    /// - each MIGRATION entry:
    ///     * has a non-empty "body" containing the Up() method text,
    ///     * has structured fields:
    ///         createsTables     [ "schema.table", ... ],
    ///         dropsTables       [ ... ],
    ///         addsColumns       [ { table: "schema.table", column: "Col" }, ... ],
    ///         dropsColumns      [ { table: "schema.table", column: "Col" }, ... ],
    ///         addsForeignKeys   [ { table: "schema.table", foreignKey: "FK_Name" }, ... ],
    ///         dropsForeignKeys  [ { table: "schema.table", foreignKey: "FK_Name" }, ... ].
    ///
    /// RenameColumn and raw Sql(...) are validated via the "body" text (phase 1: no structured summary required).
    ///
    /// NOTE: This is an executable specification. It will be red until migrations
    ///       are fully wired into sql_bodies.jsonl with the described schema.
    /// </summary>
    [TestClass]
    public class MigrationEndToEndTests
    {
        // When set to "1", end-to-end tests will keep their temp directories
        // so you can inspect both the generated code and the produced graph artifacts.
        private static readonly bool KeepArtifacts =
            string.Equals(
                Environment.GetEnvironmentVariable("RI_KEEP_E2E"),
                "1",
                StringComparison.Ordinal);

        [TestMethod]
        public void Migrations_ProduceBodies_AndStructuredSummaryInSqlBodiesJsonl()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "RoslynIndexer_E2E_Migrations_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Prepare folder structure
                var solutionDir = Path.Combine(testRoot, "MigrationSolution");
                var projectDir = Path.Combine(solutionDir, "MigrationProject");
                var sqlDir = Path.Combine(solutionDir, "sql");
                var tempRoot = Path.Combine(testRoot, "temp");
                var outDir = Path.Combine(testRoot, "out");

                Directory.CreateDirectory(solutionDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(sqlDir);
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(outDir);

                var solutionPath = Path.Combine(solutionDir, "MigrationSolution.sln");
                var projectPath = Path.Combine(projectDir, "MigrationProject.csproj");
                var configPath = Path.Combine(testRoot, "config_migrations.json");

                // 2) Minimal solution + project
                CreateMinimalSln(solutionPath, projectPath);
                CreateMinimalProjectFile(projectPath);

                // 3) C# model: Migration/MigrationBuilder stubs + multiple migrations
                CreateEfMigrationStubsAndMigrations(projectDir);

                // 4) Simple SQL script just to have a valid sqlRoot (not the focus here)
                CreateSqlScripts(sqlDir);

                // 5) Config: migrationsRoot points to projectDir
                CreateMigrationsConfigJson(
                    configPath,
                    solutionPath,
                    tempRoot,
                    outDir,
                    sqlDir: sqlDir,
                    migrationsRoot: projectDir);

                // 6) Run RoslynIndexer.Net9 with this config
                RunRoslynIndexerNet9WithConfig(configPath).GetAwaiter().GetResult();

                // 7) Read sql_bodies.jsonl from the produced ZIP
                var lines = ReadSqlBodiesLinesFromFirstZip(outDir);

                // 8) There must be at least one MIGRATION line
                Assert.IsTrue(
                    lines.Any(l => l.Contains("\"kind\":\"MIGRATION\"", StringComparison.Ordinal) ||
                                   l.Contains("\"kind\": \"MIGRATION\"", StringComparison.Ordinal)),
                    "Expected at least one MIGRATION entry in sql_bodies.jsonl.");

                // 9) Validate individual migrations and their structured summaries

                // V001_CreateTables: two CreateTable calls => createsTables contains dbo.Customer, sales.Order
                var migCreateTables = FindMigrationByName(lines, "V001_CreateTables");
                var createsTables = GetStringArray(migCreateTables, "createsTables");
                Assert.IsTrue(
                    createsTables.Contains("dbo.Customer", StringComparer.OrdinalIgnoreCase),
                    "V001_CreateTables.createsTables must contain 'dbo.Customer'.");
                Assert.IsTrue(
                    createsTables.Contains("sales.Order", StringComparer.OrdinalIgnoreCase),
                    "V001_CreateTables.createsTables must contain 'sales.Order'.");
                AssertBodyContains(migCreateTables, "CreateTable(");

                // V002_DropTables: two DropTable calls => dropsTables contains dbo.LegacyCustomer, sales.ObsoleteOrder
                var migDropTables = FindMigrationByName(lines, "V002_DropTables");
                var dropsTables = GetStringArray(migDropTables, "dropsTables");
                Assert.IsTrue(
                    dropsTables.Contains("dbo.LegacyCustomer", StringComparer.OrdinalIgnoreCase),
                    "V002_DropTables.dropsTables must contain 'dbo.LegacyCustomer'.");
                Assert.IsTrue(
                    dropsTables.Contains("sales.ObsoleteOrder", StringComparer.OrdinalIgnoreCase),
                    "V002_DropTables.dropsTables must contain 'sales.ObsoleteOrder'.");
                AssertBodyContains(migDropTables, "DropTable(");

                // V003_AddColumns: two AddColumn calls on dbo.Customer
                var migAddColumns = FindMigrationByName(lines, "V003_AddColumns");
                var addsColumns = GetTableColumnPairs(migAddColumns, "addsColumns");
                Assert.IsTrue(
                    addsColumns.Any(p =>
                        string.Equals(p.table, "dbo.Customer", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.column, "LoyaltyPoints", StringComparison.Ordinal)),
                    "V003_AddColumns.addsColumns must contain dbo.Customer.LoyaltyPoints.");
                Assert.IsTrue(
                    addsColumns.Any(p =>
                        string.Equals(p.table, "dbo.Customer", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.column, "Email", StringComparison.Ordinal)),
                    "V003_AddColumns.addsColumns must contain dbo.Customer.Email.");
                AssertBodyContains(migAddColumns, "AddColumn<");

                // V004_DropColumns: two DropColumn calls on dbo.Customer
                var migDropColumns = FindMigrationByName(lines, "V004_DropColumns");
                var dropsColumns = GetTableColumnPairs(migDropColumns, "dropsColumns");
                Assert.IsTrue(
                    dropsColumns.Any(p =>
                        string.Equals(p.table, "dbo.Customer", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.column, "Obsolete", StringComparison.Ordinal)),
                    "V004_DropColumns.dropsColumns must contain dbo.Customer.Obsolete.");
                Assert.IsTrue(
                    dropsColumns.Any(p =>
                        string.Equals(p.table, "dbo.Customer", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.column, "TemporaryCode", StringComparison.Ordinal)),
                    "V004_DropColumns.dropsColumns must contain dbo.Customer.TemporaryCode.");
                AssertBodyContains(migDropColumns, "DropColumn(");

                // V005_RenameColumns: two RenameColumn calls – we only assert presence in body (phase 1)
                var migRenameColumns = FindMigrationByName(lines, "V005_RenameColumns");
                AssertBodyContains(migRenameColumns, "RenameColumn(");
                AssertBodyContains(migRenameColumns, "newName: \"FullName\"");
                AssertBodyContains(migRenameColumns, "newName: \"NewCode\"");

                // V006_AddForeignKeys: two AddForeignKey calls => addsForeignKeys has FK_Order_Customer, FK_Order_Product
                var migAddFks = FindMigrationByName(lines, "V006_AddForeignKeys");
                var addsForeignKeys = GetTableForeignKeyPairs(migAddFks, "addsForeignKeys");
                Assert.IsTrue(
                    addsForeignKeys.Any(p =>
                        string.Equals(p.table, "sales.Order", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.foreignKey, "FK_Order_Customer", StringComparison.Ordinal)),
                    "V006_AddForeignKeys.addsForeignKeys must contain FK_Order_Customer on sales.Order.");
                Assert.IsTrue(
                    addsForeignKeys.Any(p =>
                        string.Equals(p.table, "sales.Order", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.foreignKey, "FK_Order_Product", StringComparison.Ordinal)),
                    "V006_AddForeignKeys.addsForeignKeys must contain FK_Order_Product on sales.Order.");
                AssertBodyContains(migAddFks, "AddForeignKey(");

                // V007_DropForeignKeys: two DropForeignKey calls => dropsForeignKeys has FK_Order_Customer, FK_Order_Product
                var migDropFks = FindMigrationByName(lines, "V007_DropForeignKeys");
                var dropsForeignKeys = GetTableForeignKeyPairs(migDropFks, "dropsForeignKeys");
                Assert.IsTrue(
                    dropsForeignKeys.Any(p =>
                        string.Equals(p.table, "sales.Order", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.foreignKey, "FK_Order_Customer", StringComparison.Ordinal)),
                    "V007_DropForeignKeys.dropsForeignKeys must contain FK_Order_Customer on sales.Order.");
                Assert.IsTrue(
                    dropsForeignKeys.Any(p =>
                        string.Equals(p.table, "sales.Order", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.foreignKey, "FK_Order_Product", StringComparison.Ordinal)),
                    "V007_DropForeignKeys.dropsForeignKeys must contain FK_Order_Product on sales.Order.");
                AssertBodyContains(migDropFks, "DropForeignKey(");

                // V008_RawSqlAndRename: two Sql(...) calls – we only assert presence in body (raw SQL)
                var migRawSql = FindMigrationByName(lines, "V008_RawSqlAndRename");
                AssertBodyContains(migRawSql, "Sql(\"UPDATE dbo.Customer SET IsActive = 1;\")");
                AssertBodyContains(migRawSql, "SET LoyaltyPoints = LoyaltyPoints + 10");
            }
            finally
            {
                CleanupTestRoot(testRoot);
            }
        }

        // ===== Local helpers for JSON inspection =====

        private static JsonElement FindMigrationByName(string[] lines, string migrationClassName)
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("kind", out var kindProp))
                    continue;

                var kind = kindProp.GetString();
                if (!string.Equals(kind, "MIGRATION", StringComparison.Ordinal))
                    continue;

                if (!root.TryGetProperty("name", out var nameProp))
                    continue;

                var name = nameProp.GetString();
                if (string.Equals(name, migrationClassName, StringComparison.Ordinal))
                {
                    // Clone so the caller can use it after JsonDocument is disposed.
                    return root.Clone();
                }
            }

            Assert.Fail($"Expected MIGRATION entry with name '{migrationClassName}' in sql_bodies.jsonl.");
            return default;
        }

        private static string[] GetStringArray(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var prop) ||
                prop.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>();
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        list.Add(value);
                }
            }

            return list.ToArray();
        }

        private static (string table, string column)[] GetTableColumnPairs(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var prop) ||
                prop.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<(string, string)>();
            }

            var list = new List<(string, string)>();

            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string table = null;
                string column = null;

                if (item.TryGetProperty("table", out var tProp) &&
                    tProp.ValueKind == JsonValueKind.String)
                {
                    table = tProp.GetString();
                }

                if (item.TryGetProperty("column", out var cProp) &&
                    cProp.ValueKind == JsonValueKind.String)
                {
                    column = cProp.GetString();
                }

                if (!string.IsNullOrWhiteSpace(table) && !string.IsNullOrWhiteSpace(column))
                    list.Add((table, column));
            }

            return list.ToArray();
        }

        private static (string table, string foreignKey)[] GetTableForeignKeyPairs(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var prop) ||
                prop.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<(string, string)>();
            }

            var list = new List<(string, string)>();

            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string table = null;
                string fk = null;

                if (item.TryGetProperty("table", out var tProp) &&
                    tProp.ValueKind == JsonValueKind.String)
                {
                    table = tProp.GetString();
                }

                if (item.TryGetProperty("foreignKey", out var fkProp) &&
                    fkProp.ValueKind == JsonValueKind.String)
                {
                    fk = fkProp.GetString();
                }

                if (!string.IsNullOrWhiteSpace(table) && !string.IsNullOrWhiteSpace(fk))
                    list.Add((table, fk));
            }

            return list.ToArray();
        }

        private static void AssertBodyContains(JsonElement migration, string snippet)
        {
            Assert.IsTrue(
                migration.TryGetProperty("body", out var bodyProp) &&
                bodyProp.ValueKind == JsonValueKind.String,
                "MIGRATION entry must contain a string 'body' property.");

            var body = bodyProp.GetString() ?? string.Empty;

            Assert.IsTrue(
                body.Contains(snippet, StringComparison.Ordinal),
                $"Expected migration body to contain snippet '{snippet}'.");
        }

        // ===== Local helpers for test solution / config / run =====

        private static void CreateMigrationsConfigJson(
            string configPath,
            string solutionPath,
            string tempRoot,
            string outDir,
            string sqlDir,
            string migrationsRoot)
        {
            var sb = new StringBuilder();

            sb.AppendLine("{");
            sb.AppendLine("  \"paths\": {");
            sb.AppendLine("    \"solution\":   " + JsonString(solutionPath) + ",");
            sb.AppendLine("    \"modelRoot\":      \"\",");
            sb.AppendLine("    \"sqlRoot\":        " + JsonString(sqlDir) + ",");
            sb.AppendLine("    \"inlineSqlRoot\":  \"\",");
            sb.AppendLine("    \"migrationsRoot\": " + JsonString(migrationsRoot) + ",");
            sb.AppendLine("    \"outRoot\":        " + JsonString(outDir) + ",");
            sb.AppendLine("    \"tempRoot\":   " + JsonString(tempRoot));
            sb.AppendLine("  },");
            sb.AppendLine("  \"dbGraph\": {");
            sb.AppendLine("    \"entityBaseTypes\": []");
            sb.AppendLine("  }");
            sb.AppendLine("}");

            File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
        }

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
        /// - stub EF-like Migration / MigrationBuilder types,
        /// - eight migrations, each containing two occurrences of a given operation type:
        ///     V001_CreateTables        -> CreateTable (2 tables)
        ///     V002_DropTables         -> DropTable   (2 tables)
        ///     V003_AddColumns         -> AddColumn   (2 columns)
        ///     V004_DropColumns        -> DropColumn  (2 columns)
        ///     V005_RenameColumns      -> RenameColumn(2 columns)   [body-only assertions]
        ///     V006_AddForeignKeys     -> AddForeignKey (2 FKs)
        ///     V007_DropForeignKeys    -> DropForeignKey(2 FKs)
        ///     V008_RawSqlAndRename    -> Sql(...) (2 statements)   [body-only assertions]
        /// </summary>
        private static void CreateEfMigrationStubsAndMigrations(string projectDir)
        {
            var code = """
namespace EfMigrationsSample
{
    // Minimal EF-like stubs: we do NOT want to pull real EF into the test.
    public abstract class Migration
    {
        protected abstract void Up(MigrationBuilder migrationBuilder);
        protected virtual void Down(MigrationBuilder migrationBuilder) { }
    }

    public sealed class MigrationBuilder
    {
        public void CreateTable(string name, string schema, object columns = null)
        {
        }

        public void DropTable(string name, string schema)
        {
        }

        public void AddColumn<T>(string name, string table, string schema, bool nullable, T defaultValue)
        {
        }

        public void DropColumn(string name, string table, string schema)
        {
        }

        public void RenameColumn(string name, string newName, string table, string schema)
        {
        }

        public void AddForeignKey(
            string name,
            string table,
            string schema,
            string principalTable,
            string principalSchema,
            string column,
            string principalColumn)
        {
        }

        public void DropForeignKey(
            string name,
            string table,
            string schema)
        {
        }

        public void Sql(string sql)
        {
        }
    }

    // === Migrations with two examples for each operation type ===

    public sealed class V001_CreateTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Customer",
                schema: "dbo",
                columns: null);

            migrationBuilder.CreateTable(
                name: "Order",
                schema: "sales",
                columns: null);
        }
    }

    public sealed class V002_DropTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegacyCustomer",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ObsoleteOrder",
                schema: "sales");
        }
    }

    public sealed class V003_AddColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LoyaltyPoints",
                table: "Customer",
                schema: "dbo",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Customer",
                schema: "dbo",
                nullable: true,
                defaultValue: null);
        }
    }

    public sealed class V004_DropColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Obsolete",
                table: "Customer",
                schema: "dbo");

            migrationBuilder.DropColumn(
                name: "TemporaryCode",
                table: "Customer",
                schema: "dbo");
        }
    }

    public sealed class V005_RenameColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Name",
                newName: "FullName",
                table: "Customer",
                schema: "dbo");

            migrationBuilder.RenameColumn(
                name: "Code",
                newName: "NewCode",
                table: "Customer",
                schema: "dbo");
        }
    }

    public sealed class V006_AddForeignKeys : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_Order_Customer",
                table: "Order",
                schema: "sales",
                principalTable: "Customer",
                principalSchema: "dbo",
                column: "CustomerId",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Order_Product",
                table: "Order",
                schema: "sales",
                principalTable: "Product",
                principalSchema: "dbo",
                column: "ProductId",
                principalColumn: "Id");
        }
    }

    public sealed class V007_DropForeignKeys : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Order_Customer",
                table: "Order",
                schema: "sales");

            migrationBuilder.DropForeignKey(
                name: "FK_Order_Product",
                table: "Order",
                schema: "sales");
        }
    }

    public sealed class V008_RawSqlAndRename : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE dbo.Customer SET IsActive = 1;");

            migrationBuilder.Sql(@"
UPDATE dbo.Customer
SET LoyaltyPoints = LoyaltyPoints + 10
WHERE IsVip = 1;
");
        }
    }
}
""";

            var filePath = Path.Combine(projectDir, "EfMigrationsSample.cs");
            File.WriteAllText(filePath, code, Encoding.UTF8);
        }

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
        /// Unpacks the first ZIP from outDir and returns all lines from docs/sql_bodies.jsonl.
        /// </summary>
        private static string[] ReadSqlBodiesLinesFromFirstZip(string outDir)
        {
            var zipFiles = Directory.EnumerateFiles(outDir, "*.zip").ToList();
            Assert.IsTrue(zipFiles.Count >= 1,
                "Expected at least one ZIP archive in the 'out' folder for migrations run.");

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
                "Expected sql_bodies.jsonl to be produced inside ZIP for migrations run.");

            return File.ReadAllLines(sqlBodiesPath);
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
