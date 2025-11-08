using System.Collections.Generic;

namespace RoslynIndexer.Core.Models
{
    /// <summary>
    /// Compact result of Roslyn analysis (kept intentionally small for RAG).
    /// </summary>
    public sealed class CSharpAnalysis
    {
        public int ProjectCount { get; set; }
        public int DocumentCount { get; set; }
        public int ClassCount { get; set; }
        public int MethodCount { get; set; }
        public Dictionary<string, int> PerProjectDocuments { get; set; } = new();
    }
}
