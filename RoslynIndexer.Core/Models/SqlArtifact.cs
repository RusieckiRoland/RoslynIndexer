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

        /// <summary>
        /// Optional C# context for inline SQL or other advanced scenarios.
        /// These fields are null for plain SQL files and migrations.
        /// </summary>
        public string? Namespace { get; set; }
        public string? TypeFullName { get; set; }
        public string? MethodFullName { get; set; }
        public int? LineNumber { get; set; }
        public string? RelativePath { get; set; }

        /// <summary>
        /// Optional raw SQL body and origin for InlineSQL artifacts.
        /// For legacy TSQL / EF-Migration artifacts these remain null.
        /// </summary>
        public string? Body { get; set; }

        /// <summary>
        /// Describes where the inline SQL body came from:
        /// "HotMethod", "ExtraHotMethod", "HeuristicRoslyn", "HeuristicFallback".
        /// </summary>
        public string? BodyOrigin { get; set; }

        public SqlArtifact(string sourcePath, string artifactKind, string identifier)
        {
            SourcePath = sourcePath;
            ArtifactKind = artifactKind;
            Identifier = identifier;
        }
    }
}
