// RoslynIndexer.Tests/EfMigrations/SeedEfMigrations.cs
using System;
using System.IO;
using System.Text;

namespace RoslynIndexer.Tests.EfMigrations
{
    /// <summary>
    /// Seeds a minimal set of EF-like migrations for testing Migration analyzer.
    /// Files are C# only, no csproj/solution required for syntax-based analysis.
    /// </summary>
    public static class SeedEfMigrations
    {
        /// <summary>
        /// Creates a folder with sample migration classes.
        /// Returns absolute path to the "Migrations" directory.
        /// </summary>
        public static string CreateSampleMigrations(string testRoot)
        {
            var migrationsRoot = Path.Combine(testRoot, "Migrations");
            Directory.CreateDirectory(migrationsRoot);

            WriteBaseMigrationStub(migrationsRoot);
            WriteInitialMigration(migrationsRoot);
            WriteAddPostSlugMigration(migrationsRoot);
            WriteSeedPostsMigration(migrationsRoot);

            return migrationsRoot;
        }

        private static void WriteBaseMigrationStub(string root)
        {
            var path = Path.Combine(root, "MigrationBase.cs");
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine("namespace EfMigrationsSample");
            sb.AppendLine("{");
            sb.AppendLine("    // Very small stub to model a Migration base class.");
            sb.AppendLine("    public abstract class Migration");
            sb.AppendLine("    {");
            sb.AppendLine("        public abstract void Up();");
            sb.AppendLine("        public abstract void Down();");
            sb.AppendLine();
            sb.AppendLine("        // Fluent-like API stubs used only for syntax analysis.");
            sb.AppendLine("        protected SchemaBuilder Create => new SchemaBuilder();");
            sb.AppendLine("        protected SchemaBuilder Alter => new SchemaBuilder();");
            sb.AppendLine("        protected DataBuilder Data => new DataBuilder();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public sealed class SchemaBuilder");
            sb.AppendLine("    {");
            sb.AppendLine("        public SchemaBuilder Table(string name) { return this; }");
            sb.AppendLine("        public SchemaBuilder TableFor<T>() { return this; }");
            sb.AppendLine("        public SchemaBuilder FromTable(string name) { return this; }");
            sb.AppendLine("        public SchemaBuilder OnTable(string name) { return this; }");
            sb.AppendLine("        public SchemaBuilder AddColumn(string name, Func<ColumnBuilder, object> column) { return this; }");
            sb.AppendLine("        public SchemaBuilder RenameColumn(string oldName, string newName) { return this; }");
            sb.AppendLine("        public SchemaBuilder DropTable(string name) { return this; }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public sealed class ColumnBuilder");
            sb.AppendLine("    {");
            sb.AppendLine("        public ColumnBuilder String() { return this; }");
            sb.AppendLine("        public ColumnBuilder Int() { return this; }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public sealed class DataBuilder");
            sb.AppendLine("    {");
            sb.AppendLine("        public void InsertEntity<T>(T entity) { }");
            sb.AppendLine("        public void UpdateEntity<T>(T entity) { }");
            sb.AppendLine("        public void DeleteEntity<T>(T entity) { }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void WriteInitialMigration(string root)
        {
            var path = Path.Combine(root, "Initial_20250101.cs");
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine("namespace EfMigrationsSample");
            sb.AppendLine("{");
            sb.AppendLine("    public sealed class Initial_20250101 : Migration");
            sb.AppendLine("    {");
            sb.AppendLine("        public override void Up()");
            sb.AppendLine("        {");
            sb.AppendLine("            Create.Table(\"Blogs\");");
            sb.AppendLine("            Create.Table(\"Posts\");");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public override void Down()");
            sb.AppendLine("        {");
            sb.AppendLine("            // Drop tables (not important for tests)");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void WriteAddPostSlugMigration(string root)
        {
            var path = Path.Combine(root, "AddPostSlug_20250102.cs");
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine("namespace EfMigrationsSample");
            sb.AppendLine("{");
            sb.AppendLine("    public sealed class AddPostSlug_20250102 : Migration");
            sb.AppendLine("    {");
            sb.AppendLine("        public override void Up()");
            sb.AppendLine("        {");
            sb.AppendLine("            Alter.Table(\"Posts\").AddColumn(\"Slug\", c => c.String());");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public override void Down()");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void WriteSeedPostsMigration(string root)
        {
            var path = Path.Combine(root, "SeedPosts_20250105.cs");
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine("namespace EfMigrationsSample");
            sb.AppendLine("{");
            sb.AppendLine("    public sealed class SeedPosts_20250105 : Migration");
            sb.AppendLine("    {");
            sb.AppendLine("        public override void Up()");
            sb.AppendLine("        {");
            sb.AppendLine("            Data.InsertEntity(new Post { Id = 1, Title = \"Hello\", BlogId = 1 });");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public override void Down()");
            sb.AppendLine("        {");
            sb.AppendLine("            Data.DeleteEntity(new Post { Id = 1 });");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public sealed class Post");
            sb.AppendLine("    {");
            sb.AppendLine("        public int Id { get; set; }");
            sb.AppendLine("        public string Title { get; set; }");
            sb.AppendLine("        public int BlogId { get; set; }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }
}
