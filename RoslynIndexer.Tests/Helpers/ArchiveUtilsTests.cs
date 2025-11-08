// ==========================
// Helpers/ArchiveUtilsTests.cs
// ==========================
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Core.Helpers;

namespace RoslynIndexer.Tests.Helpers
{
    [TestClass]
    public class ArchiveUtilsTests
    {
        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ri_zip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TestMethod]
        [TestCategory("unit")]
        public void CreateZipAndDeleteForBranch_CreatesZip_CopiesToOut_AndDeletesSource()
        {
            var src = NewTempDir();
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "b.json"), "{}");

            var outDir = NewTempDir();

            var zip = ArchiveUtils.CreateZipAndDeleteForBranch(src, "feature/x", outDir);

            // Source removed
            Assert.IsFalse(Directory.Exists(src));
            // ZIP exists in out dir and has .zip extension
            Assert.IsTrue(File.Exists(zip));
            StringAssert.EndsWith(zip, ".zip");
            StringAssert.Contains(Path.GetFullPath(zip), Path.GetFullPath(outDir));

            // Entries are at the archive root (no top-level folder)
            using var z = ZipFile.OpenRead(zip);
            var names = z.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();
            CollectionAssert.Contains(names, "a.txt");
            CollectionAssert.Contains(names, "sub/b.json");
        }
    }
}
