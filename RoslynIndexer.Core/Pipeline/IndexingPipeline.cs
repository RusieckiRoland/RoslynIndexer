using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RoslynIndexer.Core.Abstractions;
using RoslynIndexer.Core.Diagnostics;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Core.Pipeline
{
    /// <summary>
    /// Orchestrates repository scanning, file hashing, Roslyn-based C# analysis, and optional SQL artifact extraction.
    /// Front-ends provide an <see cref="IWorkspaceLoader"/> (e.g., MSBuildWorkspace) and may optionally supply
    /// an <see cref="ISqlModelExtractor"/>. Progress reporting is decoupled via <see cref="IProgressReporter"/>.
    /// </summary>
    public sealed class IndexingPipeline
    {
        private readonly IRepositoryScanner _scanner;
        private readonly IFileHasher _hasher;
        private readonly ICSharpAnalyzer _csAnalyzer;
        private readonly ISqlModelExtractor? _sqlExtractor;

        public IndexingPipeline(
            IRepositoryScanner scanner,
            IFileHasher hasher,
            ICSharpAnalyzer csAnalyzer,
            ISqlModelExtractor? sqlExtractor = null)
        {
            _scanner = scanner;
            _hasher = hasher;
            _csAnalyzer = csAnalyzer;
            _sqlExtractor = sqlExtractor;
        }

        /// <summary>
        /// Backward-compatible entry point that runs the pipeline without progress reporting.
        /// </summary>
        public Task<(List<IndexItem> files, CSharpAnalysis csharp, List<SqlArtifact> sql)> RunAsync(
            RepoPaths paths,
            IWorkspaceLoader workspaceLoader,
            CancellationToken cancellationToken)
        {
            // Delegate to the overload with a null reporter (no-op).
            return RunAsync(paths, workspaceLoader, cancellationToken, progress: null);
        }

        /// <summary>
        /// Runs the indexing pipeline with an optional progress reporter.
        /// Returns a tuple containing the file inventory, C# analysis output, and SQL artifacts (if configured).
        ///
        /// Environment variables:
        /// <list type="bullet">
        /// <item><description><c>INDEXER_NOHASH=1</c> — skip the hashing phase entirely.</description></item>
        /// </list>
        /// </summary>
        public async Task<(List<IndexItem> files, CSharpAnalysis csharp, List<SqlArtifact> sql)> RunAsync(
            RepoPaths paths,
            IWorkspaceLoader workspaceLoader,
            CancellationToken cancellationToken,
            IProgressReporter? progress)
        {
            progress ??= NullProgressReporter.Instance;

            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            System.Console.WriteLine("[Scan] Root     : " + paths.RepoRoot);
            System.Console.WriteLine("[Scan] Solution : " + paths.SolutionPath);
            System.Console.Out.Flush();

            // 1) Enumerate repository files (source, configs, SQL, etc.).
            progress.SetPhase("Scanning repository");
            var swScan = System.Diagnostics.Stopwatch.StartNew();
            var files = new List<IndexItem>(_scanner.EnumerateFiles(paths));
            swScan.Stop();
            System.Console.WriteLine("[Scan] Files found : " + files.Count + $" (in {swScan.Elapsed})");
            System.Console.Out.Flush();

            // 2) Compute content hashes for all discovered files unless disabled via INDEXER_NOHASH.
            progress.SetPhase("Hashing files");
            bool skipHash = string.Equals(
                System.Environment.GetEnvironmentVariable("INDEXER_NOHASH"),
                "1",
                System.StringComparison.Ordinal);

            if (skipHash)
            {
                System.Console.WriteLine("[Hash] SKIPPED (INDEXER_NOHASH=1)");
            }
            else
            {
                var swHash = System.Diagnostics.Stopwatch.StartNew();
                int i = 0, total = files.Count;
                progress.Report(("Hashing files", 0, total));

                foreach (var f in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using (var fs = File.OpenRead(f.AbsolutePath))
                    {
                        f.Sha256 = _hasher.ComputeSha256(fs);
                    }

                    i++;
                    // Notify external reporter with fine-grained progress.
                    progress.Report(("Hashing files", i, total));

                    // Preserve existing console batching behavior for visibility on large repos.
                    if ((i % 500) == 0 || i == total)
                    {
                        System.Console.WriteLine($"[Hash] {i}/{total}");
                        System.Console.Out.Flush();
                    }
                }

                swHash.Stop();
                System.Console.WriteLine("[Hash] Done in " + swHash.Elapsed);
            }

            // 3) Load and evaluate the solution via the provided workspace loader (design-time, no full build).
            progress.SetPhase("Opening solution");
            System.Console.WriteLine("[MSBuild] Opening solution...");
            System.Console.Out.Flush();

            var swOpen = System.Diagnostics.Stopwatch.StartNew();
            var solution = await workspaceLoader.LoadSolutionAsync(paths.SolutionPath, cancellationToken)
                                                .ConfigureAwait(false);
            swOpen.Stop();
            System.Console.WriteLine("[MSBuild] Opened in " + swOpen.Elapsed);
            System.Console.WriteLine("[MSBuild] Projects: " + System.Linq.Enumerable.Count(solution.Projects));
            System.Console.Out.Flush();

            // 4) Analyze C# code using Roslyn (syntax/semantic model per project as needed).
            progress.SetPhase("Analyzing C# (Roslyn)");
            var swCs = System.Diagnostics.Stopwatch.StartNew();
            var csharp = await _csAnalyzer.AnalyzeAsync(solution, cancellationToken).ConfigureAwait(false);
            swCs.Stop();
            System.Console.WriteLine("[C#] Analysis done in " + swCs.Elapsed);

            // 5) Optionally extract SQL artifacts (T-SQL files, EF model graph, etc.).
            progress.SetPhase("Extracting SQL artifacts");
            var swSql = System.Diagnostics.Stopwatch.StartNew();
            var sql = _sqlExtractor == null
                ? new List<SqlArtifact>()
                : new List<SqlArtifact>(_sqlExtractor.Extract(paths));
            swSql.Stop();
            System.Console.WriteLine("[SQL] Extracted " + sql.Count + " artifact(s) in " + swSql.Elapsed);

            // 6) Finish and report total elapsed time.
            swTotal.Stop();
            progress.SetPhase("Core pipeline done");
            System.Console.WriteLine("[TIME] Total: " + swTotal.Elapsed);
            System.Console.Out.Flush();

            return (files, csharp, sql);
        }
    }
}
