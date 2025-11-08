// RoslynIndexer.Core/Diagnostics/HeartbeatProgressReporter.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynIndexer.Core.Diagnostics
{
    /// <summary>
    /// Periodically writes a heartbeat line containing the current phase, optional progress (current/total),
    /// and phase elapsed time. The output target is a TextWriter (e.g., Console.Out), keeping Core platform-agnostic.
    /// </summary>
    public sealed class HeartbeatProgressReporter : IProgressReporter
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TextWriter _writer;
        private readonly TimeSpan _period;
        private readonly object _gate = new();

        private string _phase;
        private int? _current;
        private int? _total;
        private DateTime _since;

        /// <param name="writer">Destination for heartbeat lines (e.g., Console.Out).</param>
        /// <param name="initialPhase">Initial phase label.</param>
        /// <param name="periodMs">Heartbeat period in milliseconds (250..10000).</param>
        public HeartbeatProgressReporter(TextWriter writer, string initialPhase = "Starting", int periodMs = 2000)
        {
            _writer = writer ?? TextWriter.Null;
            _phase = string.IsNullOrWhiteSpace(initialPhase) ? "Starting" : initialPhase;
            _since = DateTime.UtcNow;

            // Clamp period to a safe range without using Math.Clamp for .NET Standard 2.0 compatibility.
            var ms = periodMs;
            if (ms < 250) ms = 250;
            else if (ms > 10_000) ms = 10_000;
            _period = TimeSpan.FromMilliseconds(ms);

            Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    string line;
                    lock (_gate)
                    {
                        var pct = (_current.HasValue && _total.HasValue && _total > 0)
                            ? $"{(int)Math.Round(_current.Value * 100.0 / _total.Value),3}%"
                            : "   -";
                        var prog = (_current.HasValue && _total.HasValue)
                            ? $" [{_current}/{_total}]"
                            : "";
                        var elapsed = DateTime.UtcNow - _since;
                        line = $"[HB {DateTime.Now:HH:mm:ss}] {_phase}{prog} ({pct}), elapsed {elapsed:hh\\:mm\\:ss}";
                    }

                    try { await _writer.WriteLineAsync(line).ConfigureAwait(false); } catch { /* ignore */ }
                    try { await Task.Delay(_period, _cts.Token).ConfigureAwait(false); } catch { /* canceled */ }
                }
            }, CancellationToken.None);
        }

        public void SetPhase(string phase) => Report((phase, _current, _total));

        public void Report((string phase, int? current, int? total) p)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(p.phase) && !string.Equals(p.phase, _phase, StringComparison.Ordinal))
                {
                    _phase = p.phase;
                    _since = DateTime.UtcNow;
                }
                _current = p.current;
                _total = p.total;
            }
        }

        public void Dispose() => _cts.Cancel();
    }
}
