using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Core.Models;
using RoslynIndexer.Core.Services;

namespace RoslynIndexer.Core.Tests.Services
{
    [TestClass]
    public class RepositoryScannerTests
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ri_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TestMethod]
        public void EnumerateFiles_FiltersAndRelativizesCorrectly()
        {
            var root = NewTempDir();
            var sub = Path.Combine(root, "src"); Directory.CreateDirectory(sub);

            // Included extensions (case-insensitive)
            File.WriteAllText(Path.Combine(root, "readme.MD"), "# md");
            File.WriteAllText(Path.Combine(sub, "code.cs"), "class A { }");
            File.WriteAllText(Path.Combine(sub, "script.SQL"), "select 1;");
            File.WriteAllText(Path.Combine(root, ".editorconfig"), "root = true");
            File.WriteAllText(Path.Combine(root, "tmpl.tt"), "<#@ template #>");
            File.WriteAllText(Path.Combine(root, "data.json"), "{ }");

            // Excluded extensions
            File.WriteAllText(Path.Combine(root, "image.png"), "PNG");
            File.WriteAllText(Path.Combine(root, "trace.log"), "log");

            var scanner = new RepositoryScanner();
            var paths = new RepoPaths(root, solutionPath: Path.Combine(root, "dummy.sln"));

            var items = scanner.EnumerateFiles(paths).ToArray();

            // Should ignore .png and .log
            Assert.IsTrue(items.All(i => i.Kind != "PNG" && !i.RelativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(items.All(i => !i.RelativePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase)));

            // Should include known ones and set relative path
            Assert.IsTrue(items.Any(i => i.RelativePath == "readme.MD" && i.Kind == "MD"));
            Assert.IsTrue(items.Any(i => i.RelativePath.EndsWith(Path.Combine("src", "code.cs")) && i.Kind == "CS"));
            Assert.IsTrue(items.Any(i => i.RelativePath.EndsWith(Path.Combine("src", "script.SQL")) && i.Kind == "SQL"));
            Assert.IsTrue(items.Any(i => i.RelativePath.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)));

            // LastWriteUtc & SizeBytes populated
            Assert.IsTrue(items.All(i => i.SizeBytes >= 0));
        }

        [TestMethod]
        public void EnumerateFiles_NonExistingRoot_IsCreatedAndEmpty()
        {
            var root = Path.Combine(Path.GetTempPath(), "ri_tests_" + Guid.NewGuid().ToString("N"));
            // ensure it does not exist
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

            var scanner = new RepositoryScanner();
            var paths = new RepoPaths(root, solutionPath: Path.Combine(root, "dummy.sln"));

            var items = scanner.EnumerateFiles(paths).ToArray();

            Assert.IsTrue(Directory.Exists(root), "Scanner should create missing root directory");
            Assert.AreEqual(0, items.Length, "Empty root should enumerate no files");
        }
    }
}
