namespace RoslynIndexer.Core.Models
{
    /// <summary>
    /// Represents a code chunk (member) extracted from the solution, with minimal metadata for RAG.
    /// </summary>
    public sealed class ChunkEntry
    {
        public int Id { get; set; }
        public string File { get; set; }
        public string Class { get; set; }
        public string Member { get; set; }
        public string Type { get; set; }
        public string Signature { get; set; }
        public string Text { get; set; }

        // Git/meta context
        public string Branch { get; set; }
        public string HeadSha { get; set; }
        public string RepoRelativePath { get; set; }
    }
}
