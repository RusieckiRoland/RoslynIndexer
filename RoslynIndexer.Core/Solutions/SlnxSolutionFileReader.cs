using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace RoslynIndexer.Core.Solutions
{
    /// <summary>
    /// Reader for Visual Studio .slnx solution files (XML-based).
    /// It scans all elements for a "Path" attribute that points to a known project type.
    /// </summary>
    public sealed class SlnxSolutionFileReader : ISolutionFileReader
    {
        public bool CanRead(string solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
                return false;

            var ext = Path.GetExtension(solutionPath);
            return string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase);
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

            XDocument document;

            using (var stream = File.OpenRead(fullSolutionPath))
            {
                document = XDocument.Load(stream);
            }

            if (document.Root == null)
                return new SolutionFileInfo(fullSolutionPath, projectPaths);

            // The exact .slnx schema can evolve; instead of relying on specific element names,
            // we scan all elements that have a "Path" attribute pointing to a supported project file.
            foreach (var element in document.Root.Descendants())
            {
                var pathAttribute = element.Attribute("Path");
                if (pathAttribute == null)
                    continue;

                var projectPathValue = pathAttribute.Value;
                if (string.IsNullOrWhiteSpace(projectPathValue))
                    continue;

                if (!SolutionProjectExtensions.IsSupportedProjectExtension(projectPathValue))
                    continue;

                var combined = Path.IsPathRooted(projectPathValue)
                    ? projectPathValue
                    : Path.Combine(solutionDirectory, projectPathValue);

                var normalized = Path.GetFullPath(combined);
                projectPaths.Add(normalized);
            }

            return new SolutionFileInfo(fullSolutionPath, projectPaths);
        }
    }
}
