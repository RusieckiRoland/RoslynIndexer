using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoslynIndexer.Core.Solutions
{
    /// <summary>
    /// Represents a solution file and the projects it contains.
    /// Paths are stored as full, normalized paths.
    /// </summary>
    public sealed class SolutionFileInfo
    {
        public string SolutionPath { get; }

        public IReadOnlyList<string> ProjectPaths { get; }

        public SolutionFileInfo(string solutionPath, IEnumerable<string> projectPaths)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
                throw new ArgumentException("Solution path must be non-empty.", nameof(solutionPath));

            SolutionPath = Path.GetFullPath(solutionPath);

            var normalizedProjects = (projectPaths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            ProjectPaths = normalizedProjects;
        }
    }
}
