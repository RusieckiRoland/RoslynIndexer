using System.IO;

namespace RoslynIndexer.Core.Abstractions
{
    /// <summary>
    /// Computes content hashes in a streaming fashion.
    /// </summary>
    public interface IFileHasher
    {
        string ComputeSha256(Stream input);
    }
}
