using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynIndexer.Core.Abstractions;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Core.Services
{
    /// <summary>
    /// Lightweight Roslyn analyzer: counts projects, documents, classes, methods.
    /// Extend later with symbols/call-graph if needed.
    /// </summary>
    public sealed class CSharpAnalyzer : ICSharpAnalyzer
    {
        public async Task<CSharpAnalysis> AnalyzeAsync(Solution solution, CancellationToken cancellationToken)
        {
            var result = new CSharpAnalysis
            {
                ProjectCount = solution.Projects.Count()
            };

            foreach (var project in solution.Projects)
            {
                int docCount = 0;
                foreach (var doc in project.Documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!doc.SupportsSyntaxTree) continue;

                    var tree = await doc.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                    if (tree is null) continue;

                    var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    docCount++;

                    result.ClassCount += root.DescendantNodes().OfType<ClassDeclarationSyntax>().Count();
                    result.MethodCount += root.DescendantNodes().OfType<MethodDeclarationSyntax>().Count();
                }

                result.DocumentCount += docCount;
                result.PerProjectDocuments[project.Name] = docCount;
            }

            return result;
        }
    }
}
