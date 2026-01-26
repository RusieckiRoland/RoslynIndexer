// RoslynIndexer.Core/Helpers/GitProbe.cs
// Portable Git metadata probe: tries `git` CLI first, then falls back to reading .git files.
// NetStandard 2.0, no native dependencies.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace RoslynIndexer.Core.Helpers
{
    public static class GitProbe
    {
        public sealed class GitInfo
        {
            public string Branch { get; set; } = string.Empty;
            public string HeadSha { get; set; } = string.Empty;
            public string RepoRoot { get; set; } = string.Empty;
        }

        /// <summary>
        /// Try to detect repo root, branch and HEAD sha for the solution.
        /// Safe: returns empty fields if anything fails.
        /// </summary>
        public static GitInfo TryProbe(string solutionPath)
        {
            try
            {
                var slnDir = Path.GetDirectoryName(solutionPath);
                if (string.IsNullOrEmpty(slnDir) || !Directory.Exists(slnDir))
                    return new GitInfo();

                // 1) Ask `git` first (fastest and most reliable when available)
                var root = RunGit("rev-parse --show-toplevel", slnDir)?.Trim();
                if (string.IsNullOrEmpty(root))
                    root = TryFindRepoRootUpwards(slnDir);

                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    return new GitInfo(); // not a git repo

                var headSha = RunGit("rev-parse HEAD", root)?.Trim();
                var branch = RunGit("rev-parse --abbrev-ref HEAD", root)?.Trim();

                // Prefer tag name as snapshot label if HEAD is exactly at a tag.
                var tag = RunGit("describe --tags --exact-match", root)?.Trim();
                if (string.IsNullOrWhiteSpace(tag))
                {
                    var tags = RunGit("tag --points-at HEAD", root);
                    if (!string.IsNullOrWhiteSpace(tags))
                    {
                        tag = tags
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault();
                    }
                }

                if (!string.IsNullOrWhiteSpace(tag))
                {
                    branch = tag;
                }

                // 2) Fallback to .git files if CLI failed/absent
                if (string.IsNullOrEmpty(headSha) || string.IsNullOrEmpty(branch))
                {
                    var fbi = ReadGitFiles(root);
                    if (string.IsNullOrEmpty(branch)) branch = fbi.Branch;
                    if (string.IsNullOrEmpty(headSha)) headSha = fbi.HeadSha;
                }


                return new GitInfo
                {
                    Branch = branch ?? string.Empty,
                    HeadSha = headSha ?? string.Empty,
                    RepoRoot = root
                };
            }
            catch
            {
                return new GitInfo();
            }
        }

        // ---- helpers ----------------------------------------------------------

        private static string RunGit(string args, string workingDir)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    if (p.ExitCode != 0) return null;
                    return output;
                }
            }
            catch { return null; }
        }

        private static string TryFindRepoRootUpwards(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                var gitDir = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitDir) || File.Exists(gitDir))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private static GitInfo ReadGitFiles(string repoRoot)
        {
            try
            {
                // .git can be a directory or a file containing "gitdir: <path>"
                var dotGitPath = Path.Combine(repoRoot, ".git");
                string gitDir = dotGitPath;

                if (File.Exists(dotGitPath) && !Directory.Exists(dotGitPath))
                {
                    var content = File.ReadAllText(dotGitPath).Trim();
                    const string marker = "gitdir:";
                    if (content.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        var rel = content.Substring(marker.Length).Trim();
                        gitDir = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(repoRoot, rel));
                    }
                }

                // HEAD
                var headPath = Path.Combine(gitDir, "HEAD");
                if (!File.Exists(headPath)) return new GitInfo { RepoRoot = repoRoot };

                var headText = File.ReadAllText(headPath).Trim();
                if (headText.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
                {
                    var refPath = headText.Substring(4).Trim();                   // e.g. refs/heads/main
                    var branch = refPath.Split('/').Last();                      // main
                    var fullRef = Path.Combine(gitDir, refPath.Replace('/', Path.DirectorySeparatorChar));
                    string sha = null;

                    if (File.Exists(fullRef))
                    {
                        sha = File.ReadAllText(fullRef).Trim();
                    }
                    else
                    {
                        // packed-refs fallback
                        var packed = Path.Combine(gitDir, "packed-refs");
                        if (File.Exists(packed))
                        {
                            foreach (var line in File.ReadAllLines(packed))
                            {
                                if (line.StartsWith("#") || line.Length < 41) continue;
                                // "<sha> <refpath>"
                                var sp = line.Split(new[] { ' ' }, 2);
                                if (sp.Length == 2 && sp[1].Trim() == refPath)
                                {
                                    sha = sp[0].Trim();
                                    break;
                                }
                            }
                        }
                    }

                    return new GitInfo { RepoRoot = repoRoot, Branch = branch, HeadSha = sha ?? string.Empty };
                }
                else
                {
                    // Detached HEAD: HEAD contains the SHA directly
                    var sha = headText;
                    return new GitInfo { RepoRoot = repoRoot, Branch = "(detached)", HeadSha = sha };
                }
            }
            catch
            {
                return new GitInfo { RepoRoot = repoRoot };
            }
        }
    }
}
