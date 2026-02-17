using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        private const int MinSmallMemberLines = 12;
        private const int MinSmallMemberChars = 800;
        private const int MaxMethodLines = 250;
        private const int MaxMethodChars = 12000;

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

                // 0) Type rollups (one per type)
                var typeDecls = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>();
                foreach (var typeDecl in typeDecls)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) as INamedTypeSymbol;
                    var namespaceName = GetNamespaceName(typeSymbol, typeDecl);
                    var typeName = GetTypeName(typeSymbol, typeDecl);
                    var typeFqn = BuildTypeFqn(typeSymbol, typeDecl, namespaceName);

                    var rollup = BuildTypeRollupChunk(
                        typeDecl,
                        typeSymbol,
                        namespaceName,
                        typeName,
                        typeFqn,
                        filePath,
                        repoRoot,
                        document.Name,
                        projectName,
                        branchName,
                        headSha,
                        ref nextId);

                    allChunks.Add(rollup);
                }

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

                    var typeDecl = member.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
                    var typeSymbol = GetMemberTypeSymbol(symbol);
                    if (typeSymbol == null && typeDecl != null)
                        typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) as INamedTypeSymbol;

                    var namespaceName = GetNamespaceName(typeSymbol, typeDecl);
                    var typeName = GetTypeName(typeSymbol, typeDecl);
                    var typeFqn = BuildTypeFqn(typeSymbol, typeDecl, namespaceName);

                    // Extra type-level metadata for RAG
                    string baseTypeName = string.Empty;
                    string[] implementedInterfaces = Array.Empty<string>();

                    if (typeSymbol != null)
                    {
                        if (typeSymbol.BaseType != null)
                        {
                            baseTypeName = typeSymbol.BaseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                        }

                        implementedInterfaces = typeSymbol.AllInterfaces
                            .Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                            .Distinct()
                            .ToArray();
                    }

                    var memberSignatureCore = (symbol != null)
                        ? symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                        : memberName;

                    var memberSignature = string.IsNullOrWhiteSpace(typeFqn)
                        ? memberSignatureCore
                        : $"{typeFqn}.{memberSignatureCore}";

                    int primaryId = GetOrAssignPrimaryId(symbol, symbolToId, ref nextId);

                    var docComment = string.Join("",
                        member.GetLeadingTrivia()
                              .Select(t => t.ToFullString())
                              .Where(t => t.TrimStart().StartsWith("///", StringComparison.Ordinal)));

                    var fullCode = member.ToFullString();
                    var lineCount = CountLines(fullCode);
                    var isSmallMember = IsSmallMember(memberType, lineCount, fullCode.Length);
                    if (isSmallMember)
                    {
                        continue;
                    }

                    var chunkKind = GetChunkKind(memberType);
                    var memberKind = memberType.ToLowerInvariant();

                    var parts = ShouldSplitMethod(memberType, lineCount, fullCode.Length)
                        ? SplitBySize(fullCode, MaxMethodLines, MaxMethodChars)
                        : new List<string> { fullCode };

                    var partCount = parts.Count;

                    var repoRel = !string.IsNullOrEmpty(filePath)
                                  ? PathEx.GetRelativePath(repoRoot, filePath)
                                  : null;

                    for (int partIndex = 0; partIndex < partCount; partIndex++)
                    {
                        var isPrimary = partIndex == 0;
                        var chunkId = isPrimary ? primaryId : nextId++;
                        var sigWithPart = partCount > 1
                            ? $"{memberSignature} [part {partIndex + 1}/{partCount}]"
                            : memberSignature;

                        var chunkText = "// Namespace: " + namespaceName + " "
                                      + "// Type: " + typeName + " "
                                      + "// TypeFqn: " + typeFqn + " "
                                      + "// ChunkKind: " + chunkKind + " "
                                      + "// MemberKind: " + memberKind + " "
                                      + docComment + " "
                                      + parts[partIndex];

                        allChunks.Add(new ChunkEntry
                        {
                            Id = chunkId,
                            File = document.Name,
                            Class = typeName,
                            Member = memberName,
                            Type = memberType,
                            Signature = sigWithPart,
                            Text = chunkText,

                            // New fields
                            ChunkKind = chunkKind,
                            TypeFqn = typeFqn,
                            MemberKind = memberKind,
                            PartIndex = partCount > 1 ? partIndex + 1 : 1,
                            PartCount = partCount > 1 ? partCount : 1,
                            IsDataTypeLike = false,
                            ProjectName = projectName,
                            BaseType = baseTypeName,
                            ImplementedInterfaces = implementedInterfaces,

                            // Git/meta context
                            Branch = branchName,
                            HeadSha = headSha,
                            RepoRelativePath = repoRel
                        });
                    }

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
                        dependencyGraph[primaryId] = related.Distinct().ToList();

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

        private static int GetOrAssignPrimaryId(ISymbol symbol, Dictionary<ISymbol, int> symbolToId, ref int nextId)
        {
            if (symbol == null)
                return nextId++;

            if (!symbolToId.TryGetValue(symbol, out var id))
            {
                id = nextId++;
                symbolToId[symbol] = id;
            }
            return id;
        }

        private static INamedTypeSymbol? GetMemberTypeSymbol(ISymbol? symbol)
        {
            if (symbol == null)
                return null;

            switch (symbol)
            {
                case IMethodSymbol ms:
                    return ms.ContainingType;
                case IPropertySymbol ps:
                    return ps.ContainingType;
                case IFieldSymbol fs:
                    return fs.ContainingType;
                case IEventSymbol es:
                    return es.ContainingType;
                default:
                    return symbol.ContainingType;
            }
        }

        private static string GetChunkKind(string memberType)
        {
            return memberType switch
            {
                "Method" => "method",
                "Constructor" => "constructor",
                "Operator" => "operator",
                "Property" => "property",
                _ => "member"
            };
        }

        private static bool IsSmallMember(string memberType, int lineCount, int charCount)
        {
            if (memberType != "Property" && memberType != "Field" && memberType != "Constructor")
                return false;

            return lineCount < MinSmallMemberLines || charCount < MinSmallMemberChars;
        }

        private static bool ShouldSplitMethod(string memberType, int lineCount, int charCount)
        {
            if (memberType != "Method" && memberType != "Constructor")
                return false;

            return lineCount > MaxMethodLines || charCount > MaxMethodChars;
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int lines = 1;
            foreach (var ch in text)
                if (ch == '\n') lines++;
            return lines;
        }

        private static List<string> SplitBySize(string text, int maxLines, int maxChars)
        {
            var parts = new List<string>();
            var lines = text.Split('\n');
            var current = new List<string>();
            int currentChars = 0;

            foreach (var line in lines)
            {
                var lineWithBreak = line + "\n";
                var wouldExceedLines = current.Count + 1 > maxLines;
                var wouldExceedChars = currentChars + lineWithBreak.Length > maxChars;

                if ((wouldExceedLines || wouldExceedChars) && current.Count > 0)
                {
                    parts.Add(string.Join("\n", current));
                    current.Clear();
                    currentChars = 0;
                }

                current.Add(line);
                currentChars += lineWithBreak.Length;
            }

            if (current.Count > 0)
                parts.Add(string.Join("\n", current));

            return parts;
        }

        private static ChunkEntry BuildTypeRollupChunk(
            BaseTypeDeclarationSyntax typeDecl,
            INamedTypeSymbol? typeSymbol,
            string namespaceName,
            string typeName,
            string typeFqn,
            string filePath,
            string repoRoot,
            string documentName,
            string projectName,
            string branchName,
            string headSha,
            ref int nextId)
        {
            var docComment = string.Join("",
                typeDecl.GetLeadingTrivia()
                        .Select(t => t.ToFullString())
                        .Where(t => t.TrimStart().StartsWith("///", StringComparison.Ordinal)));

            var attrs = string.Join(Environment.NewLine,
                typeDecl.AttributeLists.Select(a => a.ToFullString()));

            var header = BuildTypeHeader(typeDecl);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("// Namespace: " + namespaceName);
            sb.AppendLine("// Type: " + typeName);
            sb.AppendLine("// TypeFqn: " + typeFqn);
            sb.AppendLine("// ChunkKind: type_rollup");
            if (!string.IsNullOrWhiteSpace(docComment))
                sb.AppendLine(docComment.TrimEnd());
            if (!string.IsNullOrWhiteSpace(attrs))
                sb.AppendLine(attrs.TrimEnd());
            if (!string.IsNullOrWhiteSpace(header))
                sb.AppendLine(header.TrimEnd());

            var propertyCount = 0;
            var methodCount = 0;

            foreach (var member in GetTypeMembers(typeDecl))
            {
                switch (member)
                {
                    case PropertyDeclarationSyntax:
                        propertyCount++;
                        sb.AppendLine(member.ToFullString());
                        break;
                    case FieldDeclarationSyntax:
                        sb.AppendLine(member.ToFullString());
                        break;
                }
            }

            // Optional: method signatures only (API map)
            foreach (var member in GetTypeMembers(typeDecl))
            {
                switch (member)
                {
                    case MethodDeclarationSyntax md:
                        methodCount++;
                        sb.AppendLine(RenderMethodSignature(md));
                        break;
                    case ConstructorDeclarationSyntax cd:
                        methodCount++;
                        sb.AppendLine(RenderCtorSignature(cd));
                        break;
                    case OperatorDeclarationSyntax od:
                        methodCount++;
                        sb.AppendLine(RenderOperatorSignature(od));
                        break;
                }
            }

            var isDataTypeLike = propertyCount >= 8 && methodCount <= 2;
            var repoRel = !string.IsNullOrEmpty(filePath)
                ? PathEx.GetRelativePath(repoRoot, filePath)
                : null;

            return new ChunkEntry
            {
                Id = nextId++,
                File = documentName,
                Class = typeName,
                Member = typeName,
                Type = "TypeRollup",
                Signature = string.IsNullOrWhiteSpace(typeFqn) ? $"{typeName}#TYPE_ROLLUP" : $"{typeFqn}#TYPE_ROLLUP",
                Text = sb.ToString(),

                ChunkKind = "type_rollup",
                TypeFqn = typeFqn,
                MemberKind = string.Empty,
                PartIndex = 0,
                PartCount = 0,
                IsDataTypeLike = isDataTypeLike,
                ProjectName = projectName,
                BaseType = typeSymbol?.BaseType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? string.Empty,
                ImplementedInterfaces = typeSymbol != null
                    ? typeSymbol.AllInterfaces.Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).Distinct().ToArray()
                    : Array.Empty<string>(),

                Branch = branchName,
                HeadSha = headSha,
                RepoRelativePath = repoRel
            };
        }

        private static string BuildTypeHeader(BaseTypeDeclarationSyntax typeDecl)
        {
            var full = typeDecl.ToString();
            var braceIdx = full.IndexOf('{');
            if (braceIdx > 0)
                return full.Substring(0, braceIdx).Trim();

            var nlIdx = full.IndexOf('\n');
            if (nlIdx > 0)
                return full.Substring(0, nlIdx).Trim();

            return full.Trim();
        }

        private static IEnumerable<MemberDeclarationSyntax> GetTypeMembers(BaseTypeDeclarationSyntax typeDecl)
        {
            if (typeDecl is TypeDeclarationSyntax tds)
                return tds.Members;

            if (typeDecl is RecordDeclarationSyntax rds)
                return rds.Members;

            return Array.Empty<MemberDeclarationSyntax>();
        }

        private static string RenderMethodSignature(MethodDeclarationSyntax md)
        {
            var sig = md.WithBody(null)
                        .WithExpressionBody(null)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            return sig.ToFullString();
        }

        private static string RenderCtorSignature(ConstructorDeclarationSyntax cd)
        {
            var sig = cd.WithBody(null)
                        .WithExpressionBody(null)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            return sig.ToFullString();
        }

        private static string RenderOperatorSignature(OperatorDeclarationSyntax od)
        {
            var sig = od.WithBody(null)
                        .WithExpressionBody(null)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            return sig.ToFullString();
        }

        private static string GetNamespaceName(INamedTypeSymbol? typeSymbol, BaseTypeDeclarationSyntax? typeDecl)
        {
            if (typeSymbol?.ContainingNamespace != null && !typeSymbol.ContainingNamespace.IsGlobalNamespace)
                return typeSymbol.ContainingNamespace.ToDisplayString();

            var ns = typeDecl?.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
            return ns?.Name.ToString() ?? string.Empty;
        }

        private static string GetTypeName(INamedTypeSymbol? typeSymbol, BaseTypeDeclarationSyntax? typeDecl)
        {
            if (typeSymbol != null)
                return typeSymbol.Name;

            if (typeDecl is TypeDeclarationSyntax tds)
                return tds.Identifier.Text;

            if (typeDecl is RecordDeclarationSyntax rds)
                return rds.Identifier.Text;

            return "NoType";
        }

        private static string BuildTypeFqn(INamedTypeSymbol? typeSymbol, BaseTypeDeclarationSyntax? typeDecl, string namespaceName)
        {
            if (typeSymbol != null)
            {
                var typeNames = new Stack<string>();
                var cur = typeSymbol;
                while (cur != null)
                {
                    typeNames.Push(cur.Name);
                    cur = cur.ContainingType;
                }

                var typePart = string.Join(".", typeNames);
                return string.IsNullOrWhiteSpace(namespaceName) ? typePart : namespaceName + "." + typePart;
            }

            if (typeDecl != null)
            {
                var names = new List<string>();
                var cur = typeDecl;
                while (cur != null)
                {
                    names.Add(GetTypeName(null, cur));
                    cur = cur.Parent?.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
                }

                names.Reverse();
                var typePart = string.Join(".", names);
                return string.IsNullOrWhiteSpace(namespaceName) ? typePart : namespaceName + "." + typePart;
            }

            return string.Empty;
        }

    }
}
