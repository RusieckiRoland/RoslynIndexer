using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Tests.SqlScripts
{
    /// <summary>
    /// Contract tests for SqlArtifact default values.
    /// We want to guarantee that inline-only context fields
    /// stay null for "plain" SQL artifacts (e.g. TSQL files, migrations),
    /// unless an inline SQL scanner explicitly populates them.
    /// </summary>
    [TestClass]
    public class SqlArtifactDefaultsTests
    {
        [TestMethod]
        public void Constructor_LeavesInlineContextFieldsNull_ForPlainTsqlArtifact()
        {
            // Arrange
            // Simulate a regular T-SQL artifact coming from a .sql file.
            var artifact = new SqlArtifact(
                sourcePath: @"C:\temp\schema\Customer.sql",
                artifactKind: "TSQL",
                identifier: "dbo.Customer|TABLE");

            // Act
            // No additional mutation here – we only rely on the constructor.

            // Assert
            // Inline-only context should not be populated implicitly.
            Assert.IsNull(
                artifact.Namespace,
                "Namespace should remain null for plain TSQL artifacts.");

            Assert.IsNull(
                artifact.TypeFullName,
                "TypeFullName should remain null for plain TSQL artifacts.");

            Assert.IsNull(
                artifact.MethodFullName,
                "MethodFullName should remain null for plain TSQL artifacts.");

            Assert.IsNull(
                artifact.LineNumber,
                "LineNumber should remain null for plain TSQL artifacts.");

            Assert.IsNull(
                artifact.RelativePath,
                "RelativePath should remain null for plain TSQL artifacts.");
        }
    }
}
