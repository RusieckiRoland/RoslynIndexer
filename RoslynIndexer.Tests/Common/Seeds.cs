using System;
using System.IO;
using System.Text;
using RoslynIndexer.Tests.Common;

namespace RoslynIndexer.Tests.Common
{
    internal static class Seeds
    {
        public static void SeedBasicCSharp(string root, out string slnPath, out string projPath)
        {
            Directory.CreateDirectory(root);
            string projName = "Seed_BasicCSharp";
            projPath = Path.Combine(root, projName + ".csproj");
            File.WriteAllText(projPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """.Replace("\r\n", "\n"));
            File.WriteAllText(Path.Combine(root, "Program.cs"),
                "using System; class Program { static void Main() => Console.WriteLine(\"Hello\"); }");
            File.WriteAllText(Path.Combine(root, "MathUtils.cs"),
                "public static class MathUtils { public static int Add(int a,int b)=>a+b; }");

            slnPath = Path.Combine(root, projName + ".sln");
            RunnerTestHelpers.CreateMinimalSolution(slnPath, projName, projPath);
        }

        public static void SeedEfCodeFirst(string root, out string slnPath, out string projPath)
        {
            Directory.CreateDirectory(root);
            string projName = "Seed_EfCodeFirst";
            projPath = Path.Combine(root, projName + ".csproj");
            File.WriteAllText(projPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """.Replace("\r\n", "\n"));

            // Minimal stubs to avoid NuGet restore (offline & deterministic)
            File.WriteAllText(Path.Combine(root, "EFCoreStubs.cs"),
                """
                namespace Microsoft.EntityFrameworkCore
                {
                    public class DbContext { protected DbContext(){} protected virtual void OnModelCreating(ModelBuilder modelBuilder) {} }
                    public class DbSet<T> {}
                    public class ModelBuilder { public EntityTypeBuilder<TEntity> Entity<TEntity>() => new EntityTypeBuilder<TEntity>(); }
                    public class EntityTypeBuilder<TEntity>
                    {
                        public EntityTypeBuilder<TEntity> HasKey(System.Linq.Expressions.Expression<System.Func<TEntity, object>> _) => this;
                        public EntityTypeBuilder<TEntity> Property<T>(System.Linq.Expressions.Expression<System.Func<TEntity, T>> _) => this;
                        public ReferenceCollectionBuilder<TEntity, TOther> HasMany<TOther>(System.Linq.Expressions.Expression<System.Func<TEntity, System.Collections.Generic.ICollection<TOther>>> _ = null) => new();
                    }
                    public class ReferenceCollectionBuilder<TPrincipal, TDependent> { public ReferenceCollectionBuilder<TPrincipal, TDependent> WithOne(System.Linq.Expressions.Expression<System.Func<TDependent, TPrincipal>> _ = null) => this; }
                }
                """.Replace("\r\n", "\n"));

            File.WriteAllText(Path.Combine(root, "BlogContext.cs"),
                """
                using Microsoft.EntityFrameworkCore;
                using System.Collections.Generic;

                public class BlogContext : DbContext
                {
                    public DbSet<Blog> Blogs { get; set; }
                    public DbSet<Post> Posts { get; set; }

                    protected override void OnModelCreating(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<Blog>().HasKey(b => b.Id);
                        modelBuilder.Entity<Blog>().Property(b => b.Name);
                        modelBuilder.Entity<Blog>().HasMany<BlogPost>().WithOne(_ => null);
                    }
                }

                public class Blog { public int Id { get; set; } public string Name { get; set; } public List<Post> Posts { get; set; } = new(); }
                public class Post { public int Id { get; set; } public int BlogId { get; set; } public Blog Blog { get; set; } }
                public class BlogPost { public int Id { get; set; } }
                """.Replace("\r\n", "\n"));

            slnPath = Path.Combine(root, projName + ".sln");
            RunnerTestHelpers.CreateMinimalSolution(slnPath, projName, projPath);
        }

        public static void SeedEfMigrations(string root, out string slnPath, out string projPath, out string efRoot)
        {
            Directory.CreateDirectory(root);
            string projName = "Seed_EfMigrations";
            projPath = Path.Combine(root, projName + ".csproj");
            File.WriteAllText(projPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """.Replace("\r\n", "\n"));

            // EF stubs for migrations
            File.WriteAllText(Path.Combine(root, "EFCoreMigrationStubs.cs"),
                """
                namespace Microsoft.EntityFrameworkCore.Migrations
                {
                    public abstract class Migration { protected internal virtual void Up(MigrationBuilder migrationBuilder) {} protected internal virtual void Down(MigrationBuilder migrationBuilder) {} }
                    public class MigrationBuilder { public void CreateTable(string name, System.Action<TableBuilder> build) {} public void DropTable(string name) {} public void RenameColumn(string name,string table,string newName) {} }
                    public class TableBuilder { public void Column<T>(string name, bool nullable=true) {} public void PrimaryKey(string name, System.Func<object> key) {} }
                    public abstract class ModelSnapshot { }
                }
                """.Replace("\r\n", "\n"));

            // Create EF folder with Migrations
            efRoot = Path.Combine(root, "Ef");
            var migDir = Path.Combine(efRoot, "Migrations");
            Directory.CreateDirectory(migDir);

            File.WriteAllText(Path.Combine(migDir, "20250101_Initial.cs"),
                """
                using Microsoft.EntityFrameworkCore.Migrations;

                public partial class Initial : Migration
                {
                    protected internal override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.CreateTable("Blogs", t => { t.Column<int>("Id", false); t.Column<string>("Name"); t.PrimaryKey("PK_Blogs", () => null); });
                    }
                    protected internal override void Down(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.DropTable("Blogs");
                    }
                }
                """.Replace("\r\n", "\n"));

            File.WriteAllText(Path.Combine(migDir, "BlogContextModelSnapshot.cs"),
                """
                using Microsoft.EntityFrameworkCore.Migrations;
                public class BlogContextModelSnapshot : ModelSnapshot {}
                """.Replace("\r\n", "\n"));

            // Minimal code file so the project has some C#
            File.WriteAllText(Path.Combine(root, "Program.cs"), "class Program { static void Main(){} }");

            slnPath = Path.Combine(root, projName + ".sln");
            RunnerTestHelpers.CreateMinimalSolution(slnPath, projName, projPath);
        }

        public static void SeedMixedIndexing(string root, out string slnPath, out string projPath, out string sqlPath)
        {
            SeedBasicCSharp(root, out slnPath, out projPath);
            sqlPath = Path.Combine(root, "sql");
            Directory.CreateDirectory(sqlPath);
            File.WriteAllText(Path.Combine(sqlPath, "CreateTables.sql"),
                """
                CREATE TABLE Blogs(Id INT PRIMARY KEY, Name NVARCHAR(200));
                GO
                """.Replace("\r\n", "\n"));
            File.WriteAllText(Path.Combine(sqlPath, "Views.sql"),
                """
                CREATE VIEW vBlogs AS SELECT Id, Name FROM Blogs;
                GO
                """.Replace("\r\n", "\n"));
        }

        public static void SeedSqlScriptsOnly(string root, out string slnPath, out string projPath, out string sqlPath)
        {
            // Keep a tiny C# project so runner always has a .sln
            SeedBasicCSharp(root, out slnPath, out projPath);
            sqlPath = Path.Combine(root, "sql_only");
            Directory.CreateDirectory(sqlPath);
            File.WriteAllText(Path.Combine(sqlPath, "Proc_Upsert.sql"),
                """
                CREATE OR ALTER PROCEDURE dbo.UpsertBlog @Id INT, @Name NVARCHAR(200) AS
                BEGIN
                    IF EXISTS(SELECT 1 FROM Blogs WHERE Id=@Id)
                        UPDATE Blogs SET Name=@Name WHERE Id=@Id;
                    ELSE
                        INSERT INTO Blogs(Id,Name) VALUES(@Id,@Name);
                END
                GO
                """.Replace("\r\n", "\n"));
        }
    }
}
