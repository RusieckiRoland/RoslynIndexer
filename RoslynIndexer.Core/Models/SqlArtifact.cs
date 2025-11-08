namespace RoslynIndexer.Core.Models
{
    /// <summary>
    /// Represents a discovered SQL artifact to feed the index (schema/migration/inline).
    /// </summary>
    public sealed class SqlArtifact
    {
        public string SourcePath { get; }
        public string ArtifactKind { get; } // e.g., "TSQL", "EF-Migration", "InlineSQL"
        public string Identifier { get; }    // e.g., proc name / migration id / file+span
        public string? Hash { get; set; }

        public SqlArtifact(string sourcePath, string artifactKind, string identifier)
        {
            SourcePath = sourcePath;
            ArtifactKind = artifactKind;
            Identifier = identifier;
        }
    }
}
