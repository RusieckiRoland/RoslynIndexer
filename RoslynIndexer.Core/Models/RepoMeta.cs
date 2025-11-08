using System;

namespace RoslynIndexer.Core.Models
{
    /// <summary>
    /// Repository metadata for artifact bundles (kept small and portable).
    /// </summary>
    public sealed class RepoMeta
    {
        public string Branch { get; set; }
        public string HeadSha { get; set; }
        public string RepositoryRoot { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }
}
