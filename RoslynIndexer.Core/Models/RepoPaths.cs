namespace RoslynIndexer.Core.Models
{
    /// <summary>
    /// Centralizes paths used across the pipeline. Only plain strings here.
    /// </summary>
    public sealed class RepoPaths
    {
        public string RepoRoot { get; }
        public string SolutionPath { get; }
        public string? SqlPath { get; }
        public string? EfMigrationsPath { get; }
        public string? InlineSqlPath { get; }

        public RepoPaths(
            string repoRoot,
            string solutionPath,
            string? sqlPath = null,
            string? efMigrationsPath = null,
            string? inlineSqlPath = null)
        {
            RepoRoot = repoRoot;
            SolutionPath = solutionPath;
            SqlPath = sqlPath;
            EfMigrationsPath = efMigrationsPath;
            InlineSqlPath = inlineSqlPath;
        }
    }
}
