// RoslynIndexer.Core/Diagnostics/NullProgressReporter.cs
namespace RoslynIndexer.Core.Diagnostics
{
    /// <summary>
    /// No-op reporter; useful in tests or when progress reporting is not required.
    /// </summary>
    public sealed class NullProgressReporter : IProgressReporter
    {
        public static readonly NullProgressReporter Instance = new();
        private NullProgressReporter() { }
        public void Dispose() { }
        public void Report((string phase, int? current, int? total) value) { }
        public void SetPhase(string phase) { }
    }
}
