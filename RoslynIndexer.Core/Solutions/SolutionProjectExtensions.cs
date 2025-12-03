using System;

namespace RoslynIndexer.Core.Solutions
{
    /// <summary>
    /// Shared configuration for which project file extensions we care about.
    /// </summary>
    internal static class SolutionProjectExtensions
    {
        // Extend this list if needed (e.g. .dbproj, .dtproj).
        internal static readonly string[] KnownProjectExtensions =
        {
            ".csproj",
            ".vbproj",
            ".fsproj",
            ".sqlproj"
        };

        internal static bool IsSupportedProjectExtension(string path)
        {
            var ext = System.IO.Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
                return false;

            foreach (var known in KnownProjectExtensions)
            {
                if (string.Equals(ext, known, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
