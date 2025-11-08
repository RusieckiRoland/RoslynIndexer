using System.IO;

namespace RoslynIndexer.Core.Helpers
{
    /// <summary>
    /// Writes a minimal WSL restore README, matching legacy behavior.
    /// </summary>
    internal static class ReadmeWriter
    {
        public static void WriteWslReadme(string readmePath, string branchName)
        {
            var text = "The produced file should be moved to the RAG system folder described in the RAG system documentation as \"output_dir\" e.g. \"output_dir\": \"branches\",\n" +
                       "to create a vector database based on these files, run the program build_vector_index e.g. python build_vector_index.py\n" +
                       "in the RAG system config config.json set the current branch for reading e.g. \"branch\":\"master\"";
            File.WriteAllText(readmePath, text);
        }
    }
}
