using System;
using System.Collections.Generic;
using System.IO;

namespace RoslynIndexer.Core.Solutions
{
    /// <summary>
    /// Minimal, robust reader for classic Visual Studio .sln files.
    /// Reads all Project(...) lines and extracts known project types (csproj, vbproj, etc.).
    /// </summary>
    public sealed class SlnSolutionFileReader : ISolutionFileReader
    {
        public bool CanRead(string solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
                return false;

            var ext = Path.GetExtension(solutionPath);
            return string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase);
        }

        public SolutionFileInfo Read(string solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
                throw new ArgumentException("Solution path must be non-empty.", nameof(solutionPath));

            var fullSolutionPath = Path.GetFullPath(solutionPath);

            if (!File.Exists(fullSolutionPath))
                throw new FileNotFoundException("Solution file not found.", fullSolutionPath);

            var solutionDirectory = Path.GetDirectoryName(fullSolutionPath) ?? string.Empty;
            var projectPaths = new List<string>();

            foreach (var rawLine in File.ReadLines(fullSolutionPath))
            {
                var line = rawLine?.TrimStart();
                if (string.IsNullOrEmpty(line))
                    continue;

                // Typical line:
                // Project("{GUID}") = "Name", "Relative\Path\Project.csproj", "{GUID}"
                if (!line.StartsWith("Project(\"", StringComparison.Ordinal))
                    continue;

                var firstCommaIndex = line.IndexOf(',');
                if (firstCommaIndex < 0)
                    continue;

                var secondCommaIndex = line.IndexOf(',', firstCommaIndex + 1);
                if (secondCommaIndex < 0)
                    continue;

                // The second clause between first and second comma contains the path with quotes.
                var pathClause = line.Substring(firstCommaIndex + 1, secondCommaIndex - firstCommaIndex - 1).Trim();

                var firstQuoteIndex = pathClause.IndexOf('"');
                var lastQuoteIndex = pathClause.LastIndexOf('"');

                if (firstQuoteIndex < 0 || lastQuoteIndex <= firstQuoteIndex)
                    continue;

                var relativeProjectPath = pathClause.Substring(firstQuoteIndex + 1, lastQuoteIndex - firstQuoteIndex - 1);
                if (string.IsNullOrWhiteSpace(relativeProjectPath))
                    continue;

                if (!SolutionProjectExtensions.IsSupportedProjectExtension(relativeProjectPath))
                    continue;

                var combined = Path.IsPathRooted(relativeProjectPath)
                    ? relativeProjectPath
                    : Path.Combine(solutionDirectory, relativeProjectPath);

                var normalized = Path.GetFullPath(combined);
                projectPaths.Add(normalized);
            }

            return new SolutionFileInfo(fullSolutionPath, projectPaths);
        }
    }
}
