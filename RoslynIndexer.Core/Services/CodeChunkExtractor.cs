using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynIndexer.Core.Internals;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Core.Services
{
    /// <summary>
    /// Extracts per-member chunks (methods/ctors/properties/operators) and builds an intra-solution dependency graph.
    /// Mirrors legacy behavior but kept compact and netstandard2.0-friendly.
    /// </summary>
    public sealed class CodeChunkExtractor
    {
        public async Task<(List<ChunkEntry> chunks, Dictionary<int, List<int>> deps)> ExtractAsync(
    Solution solution,
    string repoRoot,
    string branchName,
    string headSha,
    CancellationToken cancellationToken)
        {
            var allChunks = new List<ChunkEntry>();
            var dependencyGraph = new Dictionary<int, List<int>>();
            var symbolToId = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
            int nextId = 1;

            // Speed-up checks: only assemblies that belong to the solution
            var solutionAssemblies = new HashSet<string>(solution.Projects
                .Select(p => p.AssemblyName)
                .Where(a => !string.IsNullOrEmpty(a)));

            foreach (var project in solution.Projects)
            {
                // Project-level metadata
                var projectName = project.Name ?? string.Empty;

                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null) continue;

                foreach (var document in project.Documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var filePath = document.FilePath ?? string.Empty;

                    // Skip generated folders/files
                    if (filePath.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                        filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                    if (tree is null) continue;

                    var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    var semanticModel = compilation.GetSemanticModel(tree);

                    var swDoc = Stopwatch.StartNew();
                    int membersCount = 0;

                    var declarations = root
                        .DescendantNodes()
                        .OfType<MemberDeclarationSyntax>()
                        .Where(m => m is MethodDeclarationSyntax
                                 || m is ConstructorDeclarationSyntax
                                 || m is PropertyDeclarationSyntax
                                 || m is OperatorDeclarationSyntax);

                    foreach (var member in declarations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string memberName;
                        string memberType;
                        ISymbol symbol = null;

                        if (member is MethodDeclarationSyntax md)
                        {
                            memberName = md.Identifier.Text;
                            memberType = "Method";
                            symbol = semanticModel.GetDeclaredSymbol(md, cancellationToken);
                        }
                        else if (member is ConstructorDeclarationSyntax cd)
                        {
                            memberName = cd.Identifier.Text;
                            memberType = "Constructor";
                            symbol = semanticModel.GetDeclaredSymbol(cd, cancellationToken);
                        }
                        else if (member is PropertyDeclarationSyntax pd)
                        {
                            memberName = pd.Identifier.Text;
                            memberType = "Property";
                            symbol = semanticModel.GetDeclaredSymbol(pd, cancellationToken);
                        }
                        else if (member is OperatorDeclarationSyntax od)
                        {
                            memberName = od.OperatorToken.Text;
                            memberType = "Operator";
                            symbol = semanticModel.GetDeclaredSymbol(od, cancellationToken);
                        }
                        else
                        {
                            continue;
                        }

                        var classDecl = member.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                        var namespaceDecl = member.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
                        var className = classDecl != null ? classDecl.Identifier.Text : "NoClass";
                        var namespaceName = namespaceDecl != null ? namespaceDecl.Name.ToString() : "NoNamespace";

                        // Extra type-level metadata for RAG
                        string baseTypeName = string.Empty;
                        string[] implementedInterfaces = Array.Empty<string>();

                        if (classDecl != null)
                        {
                            var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, cancellationToken) as INamedTypeSymbol;
                            if (classSymbol != null)
                            {
                                if (classSymbol.BaseType != null)
                                {
                                    baseTypeName = classSymbol.BaseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                                }

                                implementedInterfaces = classSymbol.AllInterfaces
                                    .Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                                    .Distinct()
                                    .ToArray();
                            }
                        }

                        var memberSignature = (symbol != null)
                            ? $"{namespaceName}.{className}.{symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}"
                            : $"{namespaceName}.{className}.{memberName}";

                        int memberId;
                        if (symbol != null)
                        {
                            if (!symbolToId.ContainsKey(symbol))
                                symbolToId[symbol] = nextId++;
                            memberId = symbolToId[symbol];
                        }
                        else
                        {
                            memberId = nextId++;
                        }

                        var docComment = string.Join("",
                            member.GetLeadingTrivia()
                                  .Select(t => t.ToFullString())
                                  .Where(t => t.TrimStart().StartsWith("///", StringComparison.Ordinal)));

                        var fullCode = member.ToFullString();
                        var chunkText = "// Namespace: " + namespaceName + " "
                                      + "// Class: " + className + " "
                                      + "// Type: " + memberType + " "
                                      + docComment + " "
                                      + fullCode;

                        var repoRel = !string.IsNullOrEmpty(filePath)
                                      ? PathEx.GetRelativePath(repoRoot, filePath)
                                      : null;

                        allChunks.Add(new ChunkEntry
                        {
                            Id = memberId,
                            File = document.Name,
                            Class = className,
                            Member = memberName,
                            Type = memberType,
                            Signature = memberSignature,
                            Text = chunkText,

                            // New fields
                            ProjectName = projectName,
                            BaseType = baseTypeName,
                            ImplementedInterfaces = implementedInterfaces,

                            // Git/meta context
                            Branch = branchName,
                            HeadSha = headSha,
                            RepoRelativePath = repoRel
                        });

                        // Dependencies: invoked methods within solution assemblies
                        var related = new List<int>();

                        void CollectInvocations(IEnumerable<InvocationExpressionSyntax> invocations)
                        {
                            foreach (var call in invocations)
                            {
                                var callSymbol = semanticModel.GetSymbolInfo(call, cancellationToken).Symbol as IMethodSymbol;
                                if (callSymbol == null) continue;

                                var asm = callSymbol.ContainingAssembly?.Name;
                                if (asm != null && solutionAssemblies.Contains(asm))
                                {
                                    if (!symbolToId.ContainsKey(callSymbol))
                                        symbolToId[callSymbol] = nextId++;
                                    related.Add(symbolToId[callSymbol]);
                                }
                            }
                        }

                        if (member is BaseMethodDeclarationSyntax baseMethod)
                        {
                            CollectInvocations(baseMethod.DescendantNodes().OfType<InvocationExpressionSyntax>());
                        }
                        else if (member is PropertyDeclarationSyntax prop2)
                        {
                            var accessors = prop2.AccessorList?.Accessors;
                            if (accessors != null)
                            {
                                foreach (var accessor in accessors)
                                {
                                    CollectInvocations(accessor.DescendantNodes().OfType<InvocationExpressionSyntax>());
                                }
                            }
                        }

                        if (related.Count > 0)
                            dependencyGraph[memberId] = related.Distinct().ToList();

                        membersCount++;
                        if ((membersCount % 200) == 0)
                        {
                            Debug.WriteLine($"[Chunk] {membersCount} in {document.Name}");
                        }
                    }

                    swDoc.Stop();
                }
            }

            return (allChunks, dependencyGraph);
        }

    }
}
