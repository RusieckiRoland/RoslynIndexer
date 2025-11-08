using System;
using System.Collections.Generic;
using System.IO;
using RoslynIndexer.Core.Abstractions;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Core.Services
{
    /// <summary>
    /// Simple repo scanner with conservative defaults for code-related extensions.
    /// </summary>
    public sealed class RepositoryScanner : IRepositoryScanner
    {
        // Keep the list short and focused; can be expanded in front-ends if needed.
        private static readonly HashSet<string> _known =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".cs",".csx",".xaml",".axaml",".json",".md",".sql",".tt",".editorconfig",".props",".targets"
            };

        public IEnumerable<IndexItem> EnumerateFiles(RepoPaths paths)
        {
            var root = Path.GetFullPath(paths.RepoRoot);

            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root); // create empty root so enumeration is safe
                                                 // optional: nothing to scan yet, but EnumerateFiles on empty directory is safe
            }

            foreach (var abs in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(abs);
                if (!_known.Contains(ext)) continue;

                var rel = Internals.PathEx.GetRelativePath(root, abs);
                var fi = new FileInfo(abs);
                yield return new IndexItem(
                    absolutePath: abs,
                    relativePath: rel,
                    kind: ext.TrimStart('.').ToUpperInvariant(),
                    sizeBytes: fi.Length,
                    lastWriteUtc: fi.LastWriteTimeUtc
                );
            }
        }
    }
}
