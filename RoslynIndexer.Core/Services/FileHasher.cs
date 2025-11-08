using System.IO;
using System.Security.Cryptography;
using System.Text;
using RoslynIndexer.Core.Abstractions;

namespace RoslynIndexer.Core.Services
{
    /// <summary>
    /// Streaming SHA-256 hasher to handle large files robustly.
    /// </summary>
    public sealed class FileHasher : IFileHasher
    {
        public string ComputeSha256(Stream input)
        {
            using var sha = SHA256.Create();
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock(System.Array.Empty<byte>(), 0, 0);

            var sb = new StringBuilder(64);
            foreach (var b in sha.Hash!)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
