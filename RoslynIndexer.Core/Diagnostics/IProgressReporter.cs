// RoslynIndexer.Core/Diagnostics/IProgressReporter.cs
using System;

namespace RoslynIndexer.Core.Diagnostics
{
    /// <summary>
    /// Abstraction for reporting pipeline progress without binding to any I/O or UI.
    /// Implementations may log to console, files, telemetry, etc.
    /// </summary>
    public interface IProgressReporter : IProgress<(string phase, int? current, int? total)>, IDisposable
    {
        /// <summary>
        /// Sets the current phase and resets the phase timer.
        /// </summary>
        void SetPhase(string phase);
    }
}
