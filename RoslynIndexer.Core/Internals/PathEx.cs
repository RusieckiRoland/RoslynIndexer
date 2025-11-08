// PathEx.cs – polyfill for .NET Standard 2.0
using System;
using System.IO;

namespace RoslynIndexer.Core.Internals
{
    internal static class PathEx
    {
        public static string GetRelativePath(string basePath, string path)
        {
            if (string.IsNullOrEmpty(basePath)) return path ?? string.Empty;

            string baseFull = AppendSep(Path.GetFullPath(basePath));
            string pathFull = Path.GetFullPath(path ?? string.Empty);

            var baseUri = new Uri(baseFull, UriKind.Absolute);
            var pathUri = new Uri(pathFull, UriKind.Absolute);

            if (!string.Equals(baseUri.Scheme, pathUri.Scheme, StringComparison.OrdinalIgnoreCase))
                return path; // different schemes

            var relUri = baseUri.MakeRelativeUri(pathUri);
            var rel = Uri.UnescapeDataString(relUri.ToString());
            if (string.Equals(pathUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                rel = rel.Replace('/', Path.DirectorySeparatorChar);
            return rel;
        }

        private static string AppendSep(string p)
        {
            // Manual check for trailing directory separator (netstandard2.0 safe)
            if (!string.IsNullOrEmpty(p))
            {
                char last = p[p.Length - 1];
                if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
                    return p;
            }
            return p + Path.DirectorySeparatorChar;
        }
    }
}
