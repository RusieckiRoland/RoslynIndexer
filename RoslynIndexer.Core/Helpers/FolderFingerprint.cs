using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RoslynIndexer.Core.Helpers
{
    public static class FolderFingerprint
    {
        // Non-null default predicate (avoids nullable warnings).
        private static bool AlwaysInclude(string _)
        {
            return true;
        }

        /// <summary>
        /// Computes a fingerprint for:
        /// - a folder path (hashes the folder recursively),
        /// - OR a .sln path (hashes the solution directory).
        /// If solutionOrFolderPath ends with ".sln", the file name is removed by taking its directory.
        /// </summary>
        public static string ComputeForSolutionOrFolder(string solutionOrFolderPath)
        {
            return ComputeForSolutionOrFolder(solutionOrFolderPath, AlwaysInclude);
        }

        /// <summary>
        /// Computes a fingerprint for:
        /// - a folder path (hashes the folder recursively),
        /// - OR a .sln path (hashes the solution directory).
        /// If solutionOrFolderPath ends with ".sln", the file name is removed by taking its directory.
        /// </summary>
        public static string ComputeForSolutionOrFolder(string solutionOrFolderPath, Func<string, bool> includeFile)
        {
            if (string.IsNullOrEmpty(solutionOrFolderPath))
                throw new ArgumentException("solutionOrFolderPath is required.", "solutionOrFolderPath");

            if (includeFile == null)
                throw new ArgumentNullException("includeFile");

            string path = Path.GetFullPath(solutionOrFolderPath);

            // If the input looks like a .sln, hash its directory (even if the file doesn't exist).
            if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                if (dir.Length == 0)
                    throw new InvalidOperationException("Cannot determine solution directory from: " + path);

                return ComputeSha256(dir, includeFile);
            }

            // Otherwise it must be an existing directory.
            if (Directory.Exists(path))
            {
                return ComputeSha256(path, includeFile);
            }

            // Or an existing file (non-sln) -> hash its directory (useful when someone passes a random file path).
            if (File.Exists(path))
            {
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                if (dir.Length == 0)
                    throw new InvalidOperationException("Cannot determine directory from: " + path);

                return ComputeSha256(dir, includeFile);
            }

            throw new FileNotFoundException("Path does not exist as file or directory: " + path, path);
        }

        // Overload without optional params (older compiler friendly) and without passing null.
        public static string ComputeSha256(string folderPath)
        {
            return ComputeSha256(folderPath, AlwaysInclude);
        }

        /// <summary>
        /// Computes a deterministic fingerprint of a folder based on file contents + relative paths.
        /// If any file content changes / file is added / removed / renamed -> fingerprint changes.
        /// Note: hashes recursively (includes all subfolders).
        /// </summary>
        public static string ComputeSha256(string folderPath, Func<string, bool> includeFile)
        {
            if (string.IsNullOrEmpty(folderPath))
                throw new ArgumentException("folderPath is required.", "folderPath");

            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException(folderPath);

            if (includeFile == null)
                throw new ArgumentNullException("includeFile");

            string root = Path.GetFullPath(folderPath);
            root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var files = new System.Collections.Generic.List<FileEntry>();

            foreach (var fullPath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!includeFile(fullPath))
                    continue;

                var rel = ToRelativePath(root, fullPath);
                rel = rel.Replace('\\', '/');

                files.Add(new FileEntry(fullPath, rel));
            }

            files.Sort(FileEntry.CompareByRelPathOrdinal);

            using (var sha = SHA256.Create())
            {
                for (int i = 0; i < files.Count; i++)
                {
                    var f = files[i];

                    byte[] fileHash;
                    using (var stream = File.OpenRead(f.FullPath))
                    {
                        fileHash = sha.ComputeHash(stream);
                    }

                    FeedUtf8(sha, f.RelPath);
                    FeedByte(sha, 0x00);
                    FeedBytes(sha, fileHash);
                    FeedByte(sha, 0x00);
                }

                // No Array.Empty<byte>() for older frameworks
                sha.TransformFinalBlock(new byte[0], 0, 0);

                if (sha.Hash == null)
                    throw new InvalidOperationException("SHA256 hash is null after finalization.");

                return ToHex(sha.Hash);
            }
        }

        private sealed class FileEntry
        {
            public readonly string FullPath;
            public readonly string RelPath;

            public FileEntry(string fullPath, string relPath)
            {
                FullPath = fullPath;
                RelPath = relPath;
            }

            public static int CompareByRelPathOrdinal(FileEntry a, FileEntry b)
            {
                return string.Compare(a.RelPath, b.RelPath, StringComparison.Ordinal);
            }
        }

        // .NET Framework-friendly relative path (no Path.GetRelativePath needed).
        private static string ToRelativePath(string rootFullPath, string fileFullPath)
        {
            var rootUri = new Uri(AppendDirSep(rootFullPath), UriKind.Absolute);
            var fileUri = new Uri(Path.GetFullPath(fileFullPath), UriKind.Absolute);
            var relUri = rootUri.MakeRelativeUri(fileUri);
            return Uri.UnescapeDataString(relUri.ToString());
        }

        private static string AppendDirSep(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString()) ||
                path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                return path;

            return path + Path.DirectorySeparatorChar;
        }

        private static void FeedUtf8(HashAlgorithm sha, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        private static void FeedByte(HashAlgorithm sha, byte b)
        {
            var one = new byte[] { b };
            sha.TransformBlock(one, 0, 1, null, 0);
        }

        private static void FeedBytes(HashAlgorithm sha, byte[] bytes)
        {
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
