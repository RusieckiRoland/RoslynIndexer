using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Tests.Common;
using System;
using System.IO;
using System.Linq;

namespace RoslynIndexer.Tests.SqlScripts
{
    [TestClass]
    [TestCategory("E2E")]
    public class SqlScripts_RunnerE2ETests
    {
        [TestMethod]
        public void Should_Parse_SqlScripts_FromRunner()
        {
            RunnerTestHelpers.EnsureWindowsOrInconclusive();
            string testRoot = Path.Combine(Path.GetTempPath(), "RI_E2E_SQL_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);

            try
            {
                Seeds.SeedSqlScriptsOnly(testRoot, out var slnPath, out _, out var sqlPath);
                string tempRoot = Path.Combine(testRoot, "ArtifactsTemp");
                string cfg = RunnerTestHelpers.GetMsbuildConfigOrInconclusive();

                string runnerProj = RunnerTestHelpers.FindRunnerProjectOrInconclusive();
                var (exit, stdout, stderr) =
                    RunnerTestHelpers.RunDotnet(
                        $"run -c Release --project \"{runnerProj}\" -- --config \"{cfg}\" --solution \"{slnPath}\" --temp-root \"{tempRoot}\" --sql \"{sqlPath}\"",
                        Path.GetDirectoryName(runnerProj)!
                    );

                if (exit != 0)
                {
                    var head = string.Join(Environment.NewLine, (stdout + Environment.NewLine + stderr)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(80));
                    Assert.Fail($"Runner failed (exit {exit}). First lines:\n{head}");
                }

                string zipPath = tempRoot + ".zip";
                if (!File.Exists(zipPath))
                {
                    var zips = Directory.GetFiles(testRoot, "*.zip", SearchOption.AllDirectories);
                    Assert.IsTrue(zips.Any(), "Expected a ZIP to be created, none found.");
                    zipPath = zips.OrderByDescending(File.GetCreationTimeUtc).First();
                }

                RunnerTestHelpers.AssertZipHasStandardArtifacts(zipPath);
            }
            finally
            {
                try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); } catch { }
            }
        }
    }
}
