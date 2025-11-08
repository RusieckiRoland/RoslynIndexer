// RoslynIndexer.Tests/BasicCSharp/RunnerE2ETests.cs
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Tests.Common;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace RoslynIndexer.Tests.BasicCSharp
{
    [TestClass]
    [TestCategory("E2E")] // Black-box: uruchamiamy prawdziwy Net9 runner jako osobny proces
    public class RunnerE2ETests
    {
        [TestMethod]
        public void MinimalConfig_EndToEnd_ProducesZip()
        {
            RunnerTestHelpers.EnsureWindowsOrInconclusive();

            string testRoot = Path.Combine(Path.GetTempPath(), "RI_E2E_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);

            try
            {
                // 1) Seed minimalnego projektu i .sln
                Seeds.SeedBasicCSharp(testRoot, out var slnPath, out _);

                // 2) Odpal runnera bez pliku config (ścieżki tylko przez CLI)
                string tempRoot = Path.Combine(testRoot, "ArtifactsTemp");
                string runnerProj = RunnerTestHelpers.FindRunnerProjectOrInconclusive();

                var (exit, stdout, stderr) = RunnerTestHelpers.RunDotnet(
                    $"run -c Release --project \"{runnerProj}\" -- --solution \"{slnPath}\" --temp-root \"{tempRoot}\"",
                    Path.GetDirectoryName(runnerProj)!
                );

                if (exit != 0)
                {
                    var head = string.Join(Environment.NewLine, (stdout + Environment.NewLine + stderr)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(80));
                    Assert.Fail($"Runner failed (exit {exit}). First lines:\n{head}");
                }

                // 3) ZIP istnieje? (runner pakuje <tempRoot> i usuwa folder)
                string zipPath = tempRoot + ".zip";
                if (!File.Exists(zipPath))
                {
                    var zips = Directory.GetFiles(testRoot, "*.zip", SearchOption.AllDirectories);
                    Assert.IsTrue(zips.Any(), "Expected a ZIP to be created, none found.");
                    zipPath = zips.OrderByDescending(File.GetCreationTimeUtc).First();
                }

                // 4) Weryfikacja artefaktów standardowych
                RunnerTestHelpers.AssertZipHasStandardArtifacts(zipPath);
            }
            finally
            {
                try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); } catch { /* zostaw do inspekcji */ }
            }
        }

        [TestMethod]
        public void MinimalConfig_WithMsbuildOverrides_UsingExternalJson()
        {
            RunnerTestHelpers.EnsureWindowsOrInconclusive();

            // Używamy msbuild.vs.conf.json z rootu testów (Content + Copy if newer)
            string cfg = RunnerTestHelpers.GetMsbuildConfigOrInconclusive();

            string testRoot = Path.Combine(Path.GetTempPath(), "RI_E2E_MSBUILD_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);

            try
            {
                Seeds.SeedBasicCSharp(testRoot, out var slnPath, out _);
                string tempRoot = Path.Combine(testRoot, "ArtifactsTemp");

                string runnerProj = RunnerTestHelpers.FindRunnerProjectOrInconclusive();

                var (exit, stdout, stderr) = RunnerTestHelpers.RunDotnet(
                    $"run -c Release --project \"{runnerProj}\" -- --config \"{cfg}\" --solution \"{slnPath}\" --temp-root \"{tempRoot}\"",
                    Path.GetDirectoryName(runnerProj)!
                );

                if (exit != 0)
                {
                    var head = string.Join(Environment.NewLine, (stdout + Environment.NewLine + stderr)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(80));
                    Assert.Fail($"Runner failed (exit {exit}). First lines:\n{head}");
                }

                string zipPath = tempRoot + ".zip";
                Assert.IsTrue(File.Exists(zipPath), "Expected ZIP was not created with msbuild overrides.");

                RunnerTestHelpers.AssertZipHasStandardArtifacts(zipPath);
            }
            finally
            {
                try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); } catch { }
            }
        }
    }
}
