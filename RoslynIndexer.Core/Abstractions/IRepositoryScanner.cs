using System.Collections.Generic;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Core.Abstractions
{
    /// <summary>
    /// Scans repository tree and returns file descriptors (later used for hashing/ingestion).
    /// </summary>
    public interface IRepositoryScanner
    {
        IEnumerable<IndexItem> EnumerateFiles(RepoPaths paths);
    }
}
