using System;

namespace RoslynIndexer.Core.Solutions
{
    /// <summary>
    /// Abstraction for reading different solution file formats (e.g. .sln, .slnx).
    /// </summary>
    public interface ISolutionFileReader
    {
        /// <summary>
        /// Returns true if this reader supports the given solution file path.
        /// Typical implementation checks file extension.
        /// </summary>
        bool CanRead(string solutionPath);

        /// <summary>
        /// Parses the solution file and returns discovered projects.
        /// Implementations must return full, normalized project paths.
        /// </summary>
        SolutionFileInfo Read(string solutionPath);
    }
}
