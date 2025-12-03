using System;
using System.Collections.Generic;
using System.IO;

namespace RoslynIndexer.Core.Solutions
{
    /// <summary>
    /// Central entry point for loading solution files (.sln, .slnx, ...).
    /// Other parts of the system should depend on this type instead of
    /// parsing solution files directly.
    /// </summary>
    public static class SolutionFileLoader
    {
        // In the future this could be injected, but for now a simple static array is enough.
        private static readonly ISolutionFileReader[] Readers =
        {
            new SlnSolutionFileReader(),
            new SlnxSolutionFileReader()
        };

        /// <summary>
        /// Loads the given solution file using the first reader that supports it.
        /// </summary>
        public static SolutionFileInfo Load(string solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
                throw new ArgumentException("Solution path must be non-empty.", nameof(solutionPath));

            var fullPath = Path.GetFullPath(solutionPath);

            foreach (var reader in Readers)
            {
                if (!reader.CanRead(fullPath))
                    continue;

                return reader.Read(fullPath);
            }

            throw new NotSupportedException(
                $"Unsupported solution file format: '{fullPath}'. Expected .sln or .slnx.");
        }
    }
}
