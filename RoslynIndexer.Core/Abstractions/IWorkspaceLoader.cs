using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace RoslynIndexer.Core.Abstractions
{
    /// <summary>
    /// Loads a Roslyn Solution. Implemented in front-ends (Net48/Core) using MSBuildWorkspace.
    /// </summary>
    public interface IWorkspaceLoader
    {
        Task<Solution> LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken);
    }
}
