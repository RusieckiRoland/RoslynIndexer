using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Core.Sql; // InlineSqlScanner
using System;
using System.IO;
using System.Linq;

namespace RoslynIndexer.Tests.SqlScripts
{
    /// <summary>
    /// Unit tests for InlineSqlScanner – we treat these as the contract
    /// for what "inline SQL detection" means.
    /// </summary>
    [TestClass]
    public class InlineSqlScannerTests
    {
        [TestMethod]
        public void ScanDirectory_FindsAtLeastOneOccurrence_ForSimpleSelectLiteral()
        {
            // Arrange: create a temporary folder with a single C# file
            // containing an obvious inline SQL SELECT.
            var root = Path.Combine(
                Path.GetTempPath(),
                "RI_InlineSqlScanner_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            try
            {
                var code = @"using System.Data.SqlClient;

namespace InlineSqlSample
{
    public static class RawSql
    {
        public static void LoadCustomers(SqlConnection connection)
        {
            var sql = ""SELECT Id, Name FROM dbo.Customer WHERE IsActive = 1"";
            using (var cmd = new SqlCommand(sql, connection))
            {
            }
        }
    }
}
";
                var filePath = Path.Combine(root, "RawSql.cs");
                File.WriteAllText(filePath, code);

                // Act
                var occurrences = InlineSqlScanner
                    .ScanInlineSql(root)   // << TU BYŁO ScanDirectory(root)
                    .Cast<object>()        // we only care that something was detected
                    .ToList();

                // Assert
                Assert.IsTrue(
                    occurrences.Any(),
                    "Expected InlineSqlScanner to return at least one occurrence " +
                    "for a simple SELECT literal in LoadCustomers().");
            }
            finally
            {
                // Cleanup temp directory (best-effort)
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                    // ignore cleanup errors
                }
            }
        }

        [TestMethod]
        public void ScanDirectory_ReturnsEmpty_ForNonSqlStrings()
        {
            // Arrange: create a temporary folder with a C# file that contains
            // only a non-SQL, short, normal string literal.
            var root = Path.Combine(
                Path.GetTempPath(),
                "RI_InlineSqlScanner_NoSql_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            try
            {
                var code = @"namespace InlineSqlSample
{
    public static class NonSql
    {
        public static void DoNothing()
        {
            var message = ""hello world"";
        }
    }
}
";
                var filePath = Path.Combine(root, "NonSql.cs");
                File.WriteAllText(filePath, code);

                // Act
                var occurrences = InlineSqlScanner
                    .ScanInlineSql(root)   // << TU BYŁO ScanDirectory(root)
                    .Cast<object>()
                    .ToList();

                // Assert
                Assert.IsFalse(
                    occurrences.Any(),
                    "Expected InlineSqlScanner NOT to report any occurrences " +
                    "for non-SQL strings like \"hello world\".");
            }
            finally
            {
                // Cleanup temp directory (best-effort)
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                    // ignore cleanup errors
                }
            }
        }
    }
}
