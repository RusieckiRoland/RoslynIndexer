// RoslynIndexer.Net9/Adapters/MsBuildWorkspaceLoader.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynIndexer.Core.Abstractions;
using RoslynIndexer.Core.Logging;

namespace RoslynIndexer.Net9.Adapters
{
    /// <summary>
    /// Loads a Visual Studio solution using <see cref="MSBuildWorkspace"/> configured for design-time evaluation
    /// (no compilation). Provides lightweight console progress with a percentage and dot ticker.
    /// </summary>
    public sealed class MsBuildWorkspaceLoader : IWorkspaceLoader
    {
        /// <summary>
        /// Minimal, thread-safe console ticker that prints a header like "[MSBuild  42%] " and a stream of dots.
        /// It also gracefully handles error output by breaking the current ticker line and instructing the next
        /// tick to reprint the header.
        /// </summary>
        internal sealed class DotTicker
        {
            private static readonly object Gate = new object();
            private static bool _open;
            private static bool _needHeaderAfterBreak; // When true, next Tick() reprints the progress header first
            private static int _col;                   // Current column position for soft wrapping
            private static int _percent;               // Last displayed percentage
            private const int Wrap = 80;               // Soft wrap width for the dot stream

            /// <summary>
            /// Starts the ticker with an initial percentage and prints the header.
            /// </summary>
            public static void Start(int initialPercent = 0)
            {
                lock (Gate)
                {
                    if (_open) return;
                    _percent = initialPercent;
                    ConsoleLog.Info($"[MSBuild {_percent,3}%] ");
                    _open = true;
                    _col = 0;
                    _needHeaderAfterBreak = false;
                }
            }

            /// <summary>
            /// Updates the displayed percentage in-place (same line), unless a break was requested.
            /// </summary>
            public static void SetPercent(int p)
            {
                lock (Gate)
                {
                    if (!_open) return;
                    if (p == _percent) return;
                    _percent = p;

                    // After an error break, the header will be reprinted on the next Tick().
                    if (_needHeaderAfterBreak) return;

                    // Normal in-line header update (carriage return to overwrite the same line).
                    ConsoleLog.InfoLine($"\r[MSBuild {_percent,3}%] ",_percent);
                }
            }

            /// <summary>
            /// Prints a single dot and performs soft wrapping. If an error break occurred, reprints the header first.
            /// </summary>
            public static void Tick()
            {
                lock (Gate)
                {
                    if (!_open) return;

                    // After a break, reprint the header before continuing the dot stream.
                    if (_needHeaderAfterBreak)
                    {
                        Console.Write($"[MSBuild {_percent,3}%] ");
                        _needHeaderAfterBreak = false;
                        _col = 0;
                    }

                    Console.Write('.');
                    _col++;

                    // Soft wrap on a single console line: reprint the header and reset the column counter.
                    if (_col >= Wrap)
                    {
                        Console.Write($"\r[MSBuild {_percent,3}%] ");
                        _col = 0;
                    }
                }
            }

            /// <summary>
            /// Call before writing an error message. Breaks the current dot line and flags the next Tick() to
            /// reprint the progress header on a fresh line.
            /// </summary>
            public static void BreakLine()
            {
                lock (Gate)
                {
                    if (_open)
                    {
                        Console.WriteLine();
                        _needHeaderAfterBreak = true;
                        _col = 0;
                    }
                }
            }

            /// <summary>
            /// Stops the ticker and moves to a new line.
            /// </summary>
            public static void Stop()
            {
                lock (Gate)
                {
                    if (_open)
                    {
                        Console.WriteLine();
                        _open = false;
                        _col = 0;
                        _needHeaderAfterBreak = false;
                    }
                }
            }
        }

        // Progress accounting state
        private readonly object _progressGate = new object();
        private readonly HashSet<string> _seenProjects = new(StringComparer.OrdinalIgnoreCase);
        private int _totalProjects;
        private int _lastPercent;

        private sealed class ConsoleProgress : IProgress<ProjectLoadProgress>
        {
            private readonly MsBuildWorkspaceLoader _owner;
            public ConsoleProgress(MsBuildWorkspaceLoader owner) => _owner = owner;
            public void Report(ProjectLoadProgress p) => _owner.OnProgress(p);
        }

        private void OnProgress(ProjectLoadProgress p)
        {
            var path = p.FilePath;
            if (string.IsNullOrEmpty(path)) return;

            if (HasSupportedProjectExtension(path))
            {
                bool firstTime;
                int percentNow = _lastPercent;

                lock (_progressGate)
                {
                    firstTime = _seenProjects.Add(path);
                    if (firstTime && _totalProjects > 0)
                    {
                        percentNow = Math.Min(100, (int)((long)_seenProjects.Count * 100 / _totalProjects));
                        _lastPercent = percentNow;
                    }
                }

                if (firstTime && _totalProjects > 0)
                    DotTicker.SetPercent(percentNow);
            }
        }

        /// <summary>
        /// Opens a solution with MSBuild in design-time mode and returns the populated <see cref="Solution"/>.
        /// Uses environment variables to control console verbosity:
        /// <list type="bullet">
        /// <item><description><c>INDEXER_ERRORS_ONLY=1</c> — disable progress output, show errors only.</description></item>
        /// <item><description><c>INDEXER_SPINNER_RATE_MS</c> — delay between dots (20..2000 ms, default 200 ms).</description></item>
        /// </list>
        /// </summary>
        public async Task<Solution> LoadSolutionAsync(string solutionPath, CancellationToken ct)
        {
            var props = new Dictionary<string, string>
            {
                // Design-time build configuration: avoid full compilation and speed up evaluation.
                ["ExcludeRestorePackageImports"] = "true",
                ["DesignTimeBuild"] = "true",
                ["SkipCompilerExecution"] = "true",
                ["ProvideCommandLineArgs"] = "true",
                ["BuildProjectReferences"] = "false",
                ["ContinueOnError"] = "true",

                // Disable static analysis to prevent external tool invocation during load.
                ["RunCodeAnalysis"] = "false",
                ["CodeAnalysisRuleSet"] = ""
            };

            using var ws = MSBuildWorkspace.Create(props);

            ws.WorkspaceFailed += (s, e) =>
            {
                if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                {
                    DotTicker.BreakLine(); // Ensure the error is not appended to the dot line
                    ConsoleLog.Error($"[MSBuild-ERR] {e.Diagnostic.Message}");
                }
            };

            var errorsOnly = Environment.GetEnvironmentVariable("INDEXER_ERRORS_ONLY") == "1";
            var progress = errorsOnly ? null : new ConsoleProgress(this);

            _totalProjects = errorsOnly ? 0 : CountSupportedProjectsInSolution(solutionPath);
            _lastPercent = 0;
            _seenProjects.Clear();

            CancellationTokenSource? tickCts = null;

            try
            {
                if (!errorsOnly)
                {
                    DotTicker.Start(0);
                    tickCts = new CancellationTokenSource();
                    var token = tickCts.Token;
                    _ = Task.Run(async () =>
                    {
                        var delay = GetTickDelay();
                        while (!token.IsCancellationRequested)
                        {
                            DotTicker.Tick();
                            try { await Task.Delay(delay, token).ConfigureAwait(false); }
                            catch { /* cancelled */ }
                        }
                    }, CancellationToken.None);
                }

                var solution = await ws.OpenSolutionAsync(solutionPath, progress, ct).ConfigureAwait(false);
                return solution;
            }
            catch (Exception ex)
            {
                DotTicker.BreakLine(); // Also break the dot line before printing exception details
                ConsoleLog.Error($"[MSBuild-ERR] {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            finally
            {
                if (tickCts != null) tickCts.Cancel();
                DotTicker.Stop();
            }
        }

        private static bool HasSupportedProjectExtension(string path)
        {
            return path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Counts the number of supported project entries in the .sln file (C#/VB/F#) to drive progress percentage.
        /// </summary>

        private static int CountSupportedProjectsInSolution(string slnPath)
        {
            try
            {
                bool isSlnx = slnPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
                int total = 0;

                foreach (var line in File.ReadLines(slnPath))
                {
                    // Dla klasycznego .sln: tylko linie Project(...)
                    if (!isSlnx)
                    {
                        if (!line.StartsWith("Project(", StringComparison.Ordinal))
                            continue;
                    }

                    // Dla .slnx liczymy po wystąpieniach *.csproj / *.vbproj / *.fsproj
                    if (line.IndexOf(".csproj", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf(".vbproj", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf(".fsproj", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        total++;
                    }
                }

                return total;
            }
            catch
            {
                return 0;
            }
        }


        /// <summary>
        /// Returns the ticker delay in milliseconds from the environment, or a default value.
        /// </summary>
        private static int GetTickDelay()
        {
            var v = Environment.GetEnvironmentVariable("INDEXER_SPINNER_RATE_MS");
            return (int.TryParse(v, out var ms) && ms >= 20 && ms <= 2000) ? ms : 200;
        }
    }
}
