using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RoslynIndexer.Tests.Common
{
    internal static class RunnerTestHelpers
    {
        public static void EnsureWindowsOrInconclusive()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Inconclusive("Windows-only E2E (requires VS/MSBuild).");
        }

        public static string GetMsbuildConfigOrInconclusive()
        {
            var cfg = Path.Combine(AppContext.BaseDirectory, "msbuild.vs.conf.json");
            if (!File.Exists(cfg))
                Assert.Inconclusive("msbuild.vs.conf.json not found in bin. Set Build Action=Content and Copy to Output=Copy if newer.");
            return cfg;
        }

        public static string FindRunnerProjectOrInconclusive()
        {
            var csproj = FindFileUpwards("RoslynIndexer.Net9.csproj");
            if (string.IsNullOrEmpty(csproj) || !File.Exists(csproj))
                Assert.Inconclusive("RoslynIndexer.Net9.csproj not found (adjust helper or set layout).");
            return csproj;
        }

        public static (int exitCode, string stdout, string stderr) RunDotnet(string arguments, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = new Process { StartInfo = psi };
            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            return (p.ExitCode, sbOut.ToString(), sbErr.ToString());
        }

        public static void AssertZipHasStandardArtifacts(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            bool hasChunks = zip.Entries.Any(e => e.FullName.Replace('\\', '/')
                                    .EndsWith("regular_code_bundle/chunks.json", StringComparison.OrdinalIgnoreCase));
            bool hasMeta = zip.Entries.Any(e => {
                var p = e.FullName.Replace('\\', '/');
                return p.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith("regular_code_bundle/meta.json", StringComparison.OrdinalIgnoreCase);
            });

            Assert.IsTrue(hasChunks, "ZIP missing regular_code_bundle/chunks.json");
            Assert.IsTrue(hasMeta, "ZIP missing meta.json (root or regular_code_bundle)");
        }

        public static void CreateMinimalSolution(string slnPath, string projectName, string projectPath)
        {
            const string csProjectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
            string projectGuid = "{" + Guid.NewGuid().ToString().ToUpper() + "}";
            string relativeProjectPath = Path.GetFileName(projectPath);

            var sb = new StringBuilder();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sb.AppendLine("# Visual Studio Version 17");
            sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
            sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");
            sb.AppendLine($"Project(\"{csProjectTypeGuid}\") = \"{projectName}\", \"{relativeProjectPath}\", \"{projectGuid}\"");
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
            sb.AppendLine("EndGlobal");
            File.WriteAllText(slnPath, sb.ToString());
        }

        public static string FindFileUpwards(string fileName)
        {
            var dir = AppContext.BaseDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      ?? Environment.CurrentDirectory;
            for (int i = 0; i < 12; i++)
            {
                var candidate = Directory.GetFiles(dir, fileName, SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (!string.IsNullOrEmpty(candidate)) return candidate;

                var sub = Directory.GetDirectories(dir, "RoslynIndexer.Net9", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (!string.IsNullOrEmpty(sub))
                {
                    candidate = Directory.GetFiles(sub, fileName, SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (!string.IsNullOrEmpty(candidate)) return candidate;
                }
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return string.Empty;
        }
    }
}
