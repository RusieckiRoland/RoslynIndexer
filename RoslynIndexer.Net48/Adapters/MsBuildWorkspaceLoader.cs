using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynIndexer.Core.Abstractions;
using RoslynIndexer.Core.Logging;

namespace RoslynIndexer.Net48.Adapters
{
    // Loads a Roslyn Solution using MSBuildWorkspace in design-time mode (no real build).
    // Shows a single-line ticker with % progress; prints errors below as [MSBuild-ERR] lines.
    internal sealed class MsBuildWorkspaceLoader : IWorkspaceLoader
    {
        // ---------- Single-line % + dot ticker ----------

        // ---------- Single-line % + dot ticker ----------
        private sealed class DotTicker
        {
            private static readonly object Gate = new object();
            private static bool _open;
            private static bool _needHeaderAfterBreak;
            private static int _col;
            private static int _percent;
            private const int Wrap = 80;

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
                    ConsoleLog.InfoLine($"\r[MSBuild {_percent,3}%] ", _percent);
                }
            }

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



        // ---------- State for % calculation ----------
        private readonly object _progressGate = new object();
        private readonly HashSet<string> _seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _totalProjects;   // total .csproj/.vbproj/.fsproj in .sln
        private int _lastPercent;     // last printed %
        private Task _tickTask;

        private sealed class ConsoleProgress : IProgress<ProjectLoadProgress>
        {
            private readonly MsBuildWorkspaceLoader _owner;
            public ConsoleProgress(MsBuildWorkspaceLoader owner) { _owner = owner; }
            public void Report(ProjectLoadProgress p) { _owner.OnProgress(p); }
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

        public async Task<Solution> LoadSolutionAsync(string solutionPath, CancellationToken ct)
        {
            // Use design-time build props so MSBuild resolves references and provides command-line args,
            // but does not actually compile.
            var props = new Dictionary<string, string>
            {
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

            var ws = MSBuildWorkspace.Create(props);

            ws.WorkspaceFailed += (s, e) =>
            {
                if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                {
                    // Always break the ticker line first, then print the error.
                    DotTicker.BreakLine();
                    ConsoleLog.Error($"[MSBuild-ERR] {e.Diagnostic.Message}");
                }
            };

            bool errorsOnly = string.Equals(
                Environment.GetEnvironmentVariable("INDEXER_ERRORS_ONLY"), "1",
                StringComparison.Ordinal);

            var progress = errorsOnly ? null : new ConsoleProgress(this);

            // Count supported projects from the .sln to estimate % progress.
            _totalProjects = errorsOnly ? 0 : CountSupportedProjectsInSolution(solutionPath);
            _lastPercent = 0;
            _seenProjects.Clear();

            CancellationTokenSource tickCts = null;

            try
            {
                if (!errorsOnly)
                {
                    DotTicker.Start(0);
                    tickCts = new CancellationTokenSource();
                    var token = tickCts.Token;

                    // Timer-driven ticker (Thread.Sleep for .NET Framework)
                    _tickTask = Task.Run(() =>
                    {
                        int delay = GetTickDelay();
                        while (!token.IsCancellationRequested)
                        {
                            DotTicker.Tick();
                            Thread.Sleep(delay);
                        }
                    }, token);
                }

                var solution = await ws.OpenSolutionAsync(solutionPath, progress, ct).ConfigureAwait(false);
                return solution;
            }
            catch (Exception ex)
            {
                DotTicker.BreakLine();
                ConsoleLog.Error($"[MSBuild-ERR] {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            finally
            {
                if (tickCts != null) tickCts.Cancel();
                DotTicker.Stop();
                ws.Dispose();
            }
        }

        // ---------- Helpers ----------
        private static bool HasSupportedProjectExtension(string path)
        {
            return path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountSupportedProjectsInSolution(string slnPath)
        {
            try
            {
                bool isSlnx = slnPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
                int total = 0;

                foreach (var line in File.ReadLines(slnPath))
                {
                    // Dla klasycznego .sln zachowujemy dotychczasowe zachowanie:
                    // liczymy tylko linie Project(...)
                    if (!isSlnx)
                    {
                        if (!line.StartsWith("Project(", StringComparison.Ordinal))
                            continue;
                    }

                    // Dla .slnx NIE ma Project(...), ale ścieżki *.csproj itd.
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
                // Jakikolwiek problem z odczytem pliku => brak licznika, ale nie wywalamy procesu
                return 0;
            }
        }


        private static int GetTickDelay()
        {
            // Allow tuning via env var; default 200 ms (min 20, max 2000)
            var v = Environment.GetEnvironmentVariable("INDEXER_SPINNER_RATE_MS");
            int ms;
            return (int.TryParse(v, out ms) && ms >= 20 && ms <= 2000) ? ms : 200;
        }
    }
}
