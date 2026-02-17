using Newtonsoft.Json;

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

        // Extra metadata for RAG
        public string ChunkKind { get; set; }
        public string TypeFqn { get; set; }
        public string MemberKind { get; set; }
        public int PartIndex { get; set; }
        public int PartCount { get; set; }
        public bool IsDataTypeLike { get; set; }
        public string ProjectName { get; set; }
        public string BaseType { get; set; }
        public string[] ImplementedInterfaces { get; set; }

        // Git/meta context
        public string Branch { get; set; }
        public string HeadSha { get; set; }
        public string RepoRelativePath { get; set; }

        [JsonProperty("security", NullValueHandling = NullValueHandling.Ignore)]
        public ChunkSecurityMetadata? Security { get; set; }
    }
}
