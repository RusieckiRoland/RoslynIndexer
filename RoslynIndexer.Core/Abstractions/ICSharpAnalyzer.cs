using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Core.Abstractions
{
    /// <summary>
    /// Analyzes a Roslyn Solution and returns compact stats/metadata.
    /// </summary>
    public interface ICSharpAnalyzer
    {
        Task<CSharpAnalysis> AnalyzeAsync(Solution solution, CancellationToken cancellationToken);
    }
}
