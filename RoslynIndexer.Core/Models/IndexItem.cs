using System;

namespace RoslynIndexer.Core.Models
{
    /// <summary>
    /// File descriptor for repository content.
    /// </summary>
    public sealed class IndexItem
    {
        public string AbsolutePath { get; }
        public string RelativePath { get; }
        public string Kind { get; }
        public long SizeBytes { get; }
        public DateTime LastWriteUtc { get; }
        public string? Sha256 { get; set; } // filled later by hashing stage

        public IndexItem(string absolutePath, string relativePath, string kind, long sizeBytes, DateTime lastWriteUtc)
        {
            AbsolutePath = absolutePath;
            RelativePath = relativePath;
            Kind = kind;
            SizeBytes = sizeBytes;
            LastWriteUtc = lastWriteUtc;
        }
    }
}
