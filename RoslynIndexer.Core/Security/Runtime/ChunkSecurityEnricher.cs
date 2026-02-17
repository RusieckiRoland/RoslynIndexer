using System;
using System.Collections.Generic;
using System.IO;
using RoslynIndexer.Core.Models;
using RoslynIndexer.Core.Security.Engine;

namespace RoslynIndexer.Core.Security.Runtime
{
    public sealed class ChunkSecurityApplyResult
    {
        public int EnrichedChunkCount { get; }
        public IReadOnlyList<string> Warnings { get; }

        public ChunkSecurityApplyResult(int enrichedChunkCount, IReadOnlyList<string> warnings)
        {
            EnrichedChunkCount = enrichedChunkCount;
            Warnings = warnings ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Applies computed security metadata to extracted chunks.
    /// </summary>
    public static class ChunkSecurityEnricher
    {
        public static ChunkSecurityApplyResult Apply(
            IReadOnlyList<ChunkEntry> chunks,
            string? repoRoot,
            SecurityPolicyEngine engine)
        {
            if (chunks == null)
                throw new ArgumentNullException(nameof(chunks));
            if (engine == null)
                throw new ArgumentNullException(nameof(engine));

            var warnings = new List<string>();
            var warningSet = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var sourcePath = ResolvePath(chunk, repoRoot);
                var computed = engine.ComputeSecurity(sourcePath);

                chunk.Security = new ChunkSecurityMetadata
                {
                    ClassificationLabels = computed.ClassificationLabels == null
                        ? null
                        : ToArray(computed.ClassificationLabels),
                    UserLevel = computed.UserLevel,
                    AclTags = ToArray(computed.AclTags),
                    Warnings = computed.Warnings.Count == 0
                        ? null
                        : ToArray(computed.Warnings)
                };

                for (int j = 0; j < computed.Warnings.Count; j++)
                {
                    var warning = $"chunk:{chunk.Id} path:'{sourcePath}' -> {computed.Warnings[j]}";
                    if (warningSet.Add(warning))
                        warnings.Add(warning);
                }
            }

            return new ChunkSecurityApplyResult(
                enrichedChunkCount: chunks.Count,
                warnings: warnings);
        }

        private static string ResolvePath(ChunkEntry chunk, string? repoRoot)
        {
            if (!string.IsNullOrWhiteSpace(chunk.RepoRelativePath))
            {
                if (string.IsNullOrWhiteSpace(repoRoot))
                    return chunk.RepoRelativePath;

                try
                {
                    return Path.GetFullPath(Path.Combine(repoRoot, chunk.RepoRelativePath));
                }
                catch
                {
                    return Path.Combine(repoRoot, chunk.RepoRelativePath);
                }
            }

            return chunk.File ?? string.Empty;
        }

        private static string[] ToArray(IReadOnlyList<string> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<string>();

            var arr = new string[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return arr;
        }
    }
}
