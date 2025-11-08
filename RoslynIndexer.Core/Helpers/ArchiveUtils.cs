// RoslynIndexer.Core/Helpers/ArchiveUtils.cs
using System;
using System.IO;
using System.IO.Compression;

namespace RoslynIndexer.Core.Helpers
{
    /// <summary>
    /// Zip helper used by front-ends. Works on .NET Standard 2.0.
    /// </summary>
    public static class ArchiveUtils
    {
        /// <summary>
        /// Creates a .zip from <paramref name="sourceDir"/> and then deletes the directory.
        /// Returns the path to the created archive.
        /// </summary>
        public static string CreateZipAndDelete(string sourceDir, string? destinationZipPath = null, bool includeTopFolder = true)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException("Directory not found: " + sourceDir);

            var parent = Directory.GetParent(sourceDir)!.FullName;
            var baseName = new DirectoryInfo(sourceDir).Name;
            var zipPath = destinationZipPath ?? Path.Combine(parent, baseName + ".zip");

            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, includeTopFolder);

            ClearReadOnlyRecursive(sourceDir);
            Directory.Delete(sourceDir, recursive: true);
            return zipPath;
        }

        /// <summary>
        /// Creates a .zip named after the branch (e.g., "feature_x.zip") and deletes <paramref name="sourceDir"/>.
        /// The archive content is placed at the ZIP root (no top-level temp folder).
        /// </summary>
        public static string CreateZipAndDeleteForBranch(string sourceDir, string? branchName, bool includeTopFolder = false)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException("Directory not found: " + sourceDir);

            var parent = Directory.GetParent(sourceDir)!.FullName;
            var fallback = new DirectoryInfo(sourceDir).Name;
            var baseName = string.IsNullOrWhiteSpace(branchName) ? fallback : branchName;
            var safe = MakeSafeFileName(baseName);
            var desiredZipPath = Path.Combine(parent, safe + ".zip");

            // Put the contents at the archive root by default (no top-level temp folder).
            return CreateZipAndDelete(sourceDir, desiredZipPath, includeTopFolder);
        }

        /// <summary>
        /// Creates a .zip named after the branch and deletes <paramref name="sourceDir"/>.
        /// If <paramref name="outputDir"/> is provided, copies the created archive there.
        /// When a file with the same name already exists in the destination, appends a timestamp.
        /// Returns the final path (destination if copied; otherwise the created path).
        /// </summary>
        public static string CreateZipAndDeleteForBranch(string sourceDir, string? branchName, string? outputDir, bool includeTopFolder = false)
        {
            var created = CreateZipAndDeleteForBranch(sourceDir, branchName, includeTopFolder);
            if (string.IsNullOrWhiteSpace(outputDir))
                return created;

            try
            {
                Directory.CreateDirectory(outputDir!);
                var fileName = Path.GetFileName(created); // already branch-based and safe
                var target = Path.Combine(outputDir!, fileName);
                var final = ResolveCollisionWithTimestamp(target);

                if (!File.Exists(final))
                {
                    File.Copy(created, final, overwrite: false);
                    return final;
                }

                // Shouldn't happen due to ResolveCollision..., but keep a safe fallback.
                var fallback = AppendTimestamp(fileName);
                var fallbackPath = Path.Combine(outputDir!, fallback);
                File.Copy(created, fallbackPath, overwrite: false);
                return fallbackPath;
            }
            catch
            {
                // If copy fails for any reason, return the original location.
                return created;
            }
        }

        private static void ClearReadOnlyRecursive(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attr = File.GetAttributes(f);
                    if ((attr & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                }
                catch { /* ignore single file errors */ }
            }
            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attr = File.GetAttributes(d);
                    if ((attr & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(d, attr & ~FileAttributes.ReadOnly);
                }
                catch { }
            }
        }

        /// <summary>
        /// Produces a filesystem-safe filename by replacing invalid characters and slashes with underscores.
        /// </summary>
        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "artifacts";

            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();

            for (int i = 0; i < chars.Length; i++)
            {
                var ch = chars[i];

                // Normalize common branch separators first.
                if (ch == '/' || ch == '\\')
                {
                    chars[i] = '_';
                    continue;
                }

                // Replace any invalid filename character.
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (ch == invalid[j])
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }

            // Trim spaces and trailing dots which are problematic on Windows.
            var safe = new string(chars).Trim().TrimEnd('.');
            return string.IsNullOrEmpty(safe) ? "artifacts" : safe;
        }

        /// <summary>
        /// If the target path exists, returns a path with a timestamp appended before the extension.
        /// </summary>
        private static string ResolveCollisionWithTimestamp(string targetPath)
        {
            if (!File.Exists(targetPath))
                return targetPath;

            var dir = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory;
            var name = Path.GetFileNameWithoutExtension(targetPath);
            var ext = Path.GetExtension(targetPath);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var candidate = Path.Combine(dir, $"{name}_{stamp}{ext}");
            int i = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(dir, $"{name}_{stamp}_{i}{ext}");
                i++;
            }
            return candidate;
        }

        private static string AppendTimestamp(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return $"{name}_{stamp}{ext}";
        }
    }
}
