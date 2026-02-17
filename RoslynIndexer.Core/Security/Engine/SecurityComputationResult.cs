using System;
using System.Collections.Generic;

namespace RoslynIndexer.Core.Security.Engine
{
    /// <summary>
    /// Effective security metadata for a single path.
    /// </summary>
    public sealed class SecurityComputationResult
    {
        public IReadOnlyList<string>? ClassificationLabels { get; }
        public int? UserLevel { get; }
        public IReadOnlyList<string> AclTags { get; }
        public IReadOnlyList<string> Warnings { get; }

        public SecurityComputationResult(
            IReadOnlyList<string>? classificationLabels,
            int? userLevel,
            IReadOnlyList<string> aclTags,
            IReadOnlyList<string> warnings)
        {
            ClassificationLabels = classificationLabels;
            UserLevel = userLevel;
            AclTags = aclTags ?? Array.Empty<string>();
            Warnings = warnings ?? Array.Empty<string>();
        }
    }
}
