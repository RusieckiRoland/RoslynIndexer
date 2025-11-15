using System;
using System.Linq;
using System.Threading;
using RoslynIndexer.Core.Sql.EfMigrations;

namespace RoslynIndexer.Tests.EfMigrations
{
    internal static class EfMigrationsDebugAnalyzerDump
    {
        public static void DumpAnalyzerOutput(
            EfMigrationAnalyzer analyzer,
            string migrationsRoot,
            string repoRoot = null,
            CancellationToken cancellationToken = default)
        {
            var infos = analyzer.Analyze(
                migrationsRoot,
                repoRoot ?? migrationsRoot,
                cancellationToken);

            Console.WriteLine("====== EF MIGRATIONS ANALYZER OUTPUT ======");
            Console.WriteLine($"Migrations found: {infos.Count}");

            foreach (var mig in infos.OrderBy(m => m.ClassName))
            {
                Console.WriteLine($"MIGRATION: {mig.ClassName} ({mig.FileRelativePath})");

                if (mig.Attributes.Any())
                {
                    Console.WriteLine("  Attributes:");
                    foreach (var attr in mig.Attributes)
                    {
                        var values = string.Join(", ", attr.Values ?? Array.Empty<string>());
                        Console.WriteLine($"    {attr.Name}({values})");
                    }
                }

                Console.WriteLine("  UpOperations:");
                foreach (var op in mig.UpOperations)
                {
                    Console.WriteLine($"    Kind={op.Kind}, Table={op.Table}, Index={op.Index}, Raw={Trim(op.Raw, 120)}");
                }

                Console.WriteLine("  DownOperations:");
                foreach (var op in mig.DownOperations)
                {
                    Console.WriteLine($"    Kind={op.Kind}, Table={op.Table}, Index={op.Index}, Raw={Trim(op.Raw, 120)}");
                }
            }

            Console.WriteLine("====== END EF MIGRATIONS ANALYZER OUTPUT ======");
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return s ?? string.Empty;

            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
