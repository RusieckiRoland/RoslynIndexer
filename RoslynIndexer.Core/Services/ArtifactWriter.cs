using Newtonsoft.Json;
using RoslynIndexer.Core.Helpers;
using RoslynIndexer.Core.Models;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace RoslynIndexer.Core.Services
{
    /// <summary>
    /// Persists code artifacts to disk in the expected bundle layout.
    /// </summary>
    public static class ArtifactWriter
    {
        public static void WriteCodeArtifacts(
            string branchRoot,
            string codeOutDir,
            List<ChunkEntry> chunks,
            Dictionary<int, List<int>> deps,
            RepoMeta meta)
        {
            Directory.CreateDirectory(branchRoot);
            Directory.CreateDirectory(codeOutDir);

            var chunksJsonPath = Path.Combine(codeOutDir, "chunks.json");
            var depsJsonPath = Path.Combine(codeOutDir, "dependencies.json");
            var metaJsonPath = Path.Combine(branchRoot, "repo_meta.json");
            var readmePath = Path.Combine(codeOutDir, "README_WSL.txt");

            File.WriteAllText(chunksJsonPath, JsonConvert.SerializeObject(chunks, Newtonsoft.Json.Formatting.Indented));
            File.WriteAllText(depsJsonPath, JsonConvert.SerializeObject(deps, Newtonsoft.Json.Formatting.Indented));
            File.WriteAllText(metaJsonPath, JsonConvert.SerializeObject(meta, Newtonsoft.Json.Formatting.Indented));

            ReadmeWriter.WriteWslReadme(readmePath, Path.GetFileName(branchRoot));
        }
    }
}
