using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Core.Sql;


namespace RoslynIndexer.Tests.CustomEntities
{
    [TestClass]
    public class EntityBaseTypesTests
    {
        [TestMethod]
       
        public void AppendEfEdgesAndNodes_UsesConfigEntityBaseTypesToCreateEntityNodes()
        {
            // Arrange: temp root with sql, ef and out dirs
            var root = Path.Combine(Path.GetTempPath(), "RI_EntityBaseTypes_" + Guid.NewGuid().ToString("N"));
            var sqlRoot = Path.Combine(root, "sql");
            var efRoot = Path.Combine(root, "ef");
            var outDir = Path.Combine(root, "sql_bundle");

            Directory.CreateDirectory(sqlRoot);
            Directory.CreateDirectory(efRoot);
            Directory.CreateDirectory(outDir);

            // --- 1) Minimal SQL, żeby powstały TABLE ---
            File.WriteAllText(
                Path.Combine(sqlRoot, "Tables.sql"),
                """
        CREATE TABLE [dbo].[Product] ( [Id] INT NOT NULL );
        CREATE TABLE [dbo].[Customer] ( [Id] INT NOT NULL );
        """
            );

            // --- 2) EF POCO + BaseEntity + [Table] ---
            File.WriteAllText(
                Path.Combine(efRoot, "Entities.cs"),
                """
        using System.ComponentModel.DataAnnotations.Schema;

        namespace RoslynIndexer.Tests.CustomEntities
        {
            public abstract class BaseEntity
            {
                public int Id { get; set; }
            }

            [Table("Product", Schema = "dbo")]
            public class Product : BaseEntity
            {
                public string Name { get; set; }
            }

            [Table("Customer", Schema = "dbo")]
            public class Customer : BaseEntity
            {
                public string Name { get; set; }
            }

            // Not derived from BaseEntity -> must NOT be treated as ENTITY
            [Table("SomethingElse", Schema = "dbo")]
            public class NotAnEntity
            {
                public int Id { get; set; }
            }
        }
        """
            );

            // --- 3) Konfiguracja dbGraph.entityBaseTypes ---
            File.WriteAllText(
                Path.Combine(outDir, "config.json"),
                """
        {
          "dbGraph": {
            "entityBaseTypes": [
              "RoslynIndexer.Tests.CustomEntities.BaseEntity"
            ]
          }
        }
        """
            );

            string TrimQuotes(string s)
            {
                if (string.IsNullOrEmpty(s))
                    return s;
                s = s.Trim();
                if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                    return s.Substring(1, s.Length - 2);
                return s;
            }

            try
            {
                // --- 4) Odpalamy SqlEfGraphIndexer ---
                var exitCode = SqlEfGraphIndexer.Start(
                    outputDir: outDir,
                    sqlProjectRoot: sqlRoot,
                    efRoot: efRoot);

                Assert.AreEqual(0, exitCode, "SqlEfGraphIndexer failed (non-zero exit code).");

                var nodesCsv = Path.Combine(outDir, "graph", "nodes.csv");
                Assert.IsTrue(File.Exists(nodesCsv), "nodes.csv was not generated.");

                var allLines = File.ReadAllLines(nodesCsv);
                Assert.IsTrue(allLines.Length > 1, "nodes.csv does not contain any data rows.");

                // --- 4a) Detect separator (semicolon vs comma) ---
                var header = allLines[0];
                Console.WriteLine("nodes.csv HEADER: " + header);

                char separator;
                int semicolons = header.Count(c => c == ';');
                int commas = header.Count(c => c == ',');

                if (semicolons > commas && semicolons >= 1)
                    separator = ';';
                else if (commas > semicolons && commas >= 1)
                    separator = ',';
                else
                    separator = ','; // w tym pliku i tak jest comma

                Console.WriteLine($"nodes.csv detected separator: '{separator}'");

                Console.WriteLine("=== Raw nodes.csv ===");
                foreach (var line in allLines)
                    Console.WriteLine(line);
                Console.WriteLine("=====================");

                var rows = allLines
                    .Skip(1)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Split(separator))
                    .ToList();

                // Column order in NodeRow is: key,kind,name,schema,file,batch,domain,body_path
                var nodes = rows
                    .Where(parts => parts.Length >= 3)
                    .Select(parts => new
                    {
                        Key = TrimQuotes(parts[0]),
                        Kind = TrimQuotes(parts[1]),
                        Name = TrimQuotes(parts[2])
                    })
                    .ToList();

                Console.WriteLine("=== Parsed nodes ===");
                foreach (var n in nodes)
                    Console.WriteLine($"{n.Key} | {n.Kind} | {n.Name}");
                Console.WriteLine("====================");

                Console.WriteLine("=== ENTITY nodes from nodes.csv ===");
                foreach (var n in nodes.Where(n => string.Equals(n.Kind, "ENTITY", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"{n.Key} | {n.Kind} | {n.Name}");
                }
                Console.WriteLine("===================================");

                bool hasProductEntity = nodes.Any(x =>
                    string.Equals(x.Kind, "ENTITY", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Name, "Product", StringComparison.Ordinal));

                bool hasCustomerEntity = nodes.Any(x =>
                    string.Equals(x.Kind, "ENTITY", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Name, "Customer", StringComparison.Ordinal));

                bool hasNotAnEntity = nodes.Any(x =>
                    string.Equals(x.Kind, "ENTITY", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Name, "NotAnEntity", StringComparison.Ordinal));

                Assert.IsTrue(
                    hasProductEntity,
                    "ENTITY node for Product not found (should be derived from BaseEntity and picked via config).");

                Assert.IsTrue(
                    hasCustomerEntity,
                    "ENTITY node for Customer not found (should be derived from BaseEntity and picked via config).");

                Assert.IsFalse(
                    hasNotAnEntity,
                    "NotAnEntity should NOT be treated as ENTITY (does not derive from BaseEntity).");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, recursive: true);
                }
                catch
                {
                    // ignore cleanup failures
                }
            }
        }

    }
}
