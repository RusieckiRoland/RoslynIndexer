using System.Collections.Generic;
using RoslynIndexer.Core.Models;

namespace RoslynIndexer.Core.Abstractions
{
    /// <summary>
    /// Extracts SQL artifacts (schema, EF migrations, inline SQL). Implementation can use ScriptDom/EF.
    /// </summary>
    public interface ISqlModelExtractor
    {
        IEnumerable<SqlArtifact> Extract(RepoPaths paths);
    }
}
