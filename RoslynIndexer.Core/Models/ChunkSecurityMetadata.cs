using System;
using Newtonsoft.Json;

namespace RoslynIndexer.Core.Models
{
    /// <summary>
    /// Security metadata assigned to a chunk after evaluating path-based policy rules.
    /// </summary>
    public sealed class ChunkSecurityMetadata
    {
        [JsonProperty("classification_labels")]
        public string[]? ClassificationLabels { get; set; }

        [JsonProperty("user_level")]
        public int? UserLevel { get; set; }

        [JsonProperty("acl_tags")]
        public string[] AclTags { get; set; } = Array.Empty<string>();

        [JsonProperty("warnings", NullValueHandling = NullValueHandling.Ignore)]
        public string[]? Warnings { get; set; }
    }
}
