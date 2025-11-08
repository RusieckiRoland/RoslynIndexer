// ==========================
// Models/RepoPathsTests.cs
// ==========================
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Tests.Models
{
    [TestClass]
    public class RepoPathsTests
    {
        [TestMethod]
        [TestCategory("unit")]
        public void Ctor_Assigns_All_Properties()
        {
            var paths = new RepoPaths(
                repoRoot: "C:/repo",
                solutionPath: "C:/repo/app.sln",
                sqlPath: "C:/repo/sql",
                efMigrationsPath: "C:/repo/ef",
                inlineSqlPath: "C:/repo/inline");

            Assert.AreEqual("C:/repo", paths.RepoRoot);
            Assert.AreEqual("C:/repo/app.sln", paths.SolutionPath);
            Assert.AreEqual("C:/repo/sql", paths.SqlPath);
            Assert.AreEqual("C:/repo/ef", paths.EfMigrationsPath);
            Assert.AreEqual("C:/repo/inline", paths.InlineSqlPath);
        }

        [TestMethod]
        [TestCategory("unit")]
        public void Optional_Args_Default_To_Null()
        {
            var paths = new RepoPaths("C:/r", "C:/r/app.sln");
            Assert.IsNull(paths.SqlPath);
            Assert.IsNull(paths.EfMigrationsPath);
            Assert.IsNull(paths.InlineSqlPath);
        }
    }
}
