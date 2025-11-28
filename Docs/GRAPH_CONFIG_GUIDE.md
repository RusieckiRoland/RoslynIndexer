# SQL / EF / Inline SQL Graph – Configuration & Usage

This note explains how the SQL/EF graph uses `paths.modelRoot`, `dbGraph.entityBaseTypes`, `paths.inlineSqlRoot` and `inlineSql.extraHotMethods` in the config.

---

## 1. Model entities (`paths.modelRoot` + `dbGraph.entityBaseTypes`)

### 1.1 What is scanned under `paths.modelRoot`

`paths.modelRoot` should point to the root of your ORM model (for example the project that contains your EF `DbContext` and entity classes):

```jsonc
"paths": {
  "modelRoot": "D:/Repo/DataAccess"
}
```

The runner recursively scans this folder for C# files and looks for patterns that describe the data model.

### 1.2 `DbSet<T>` / `IDbSet<T>` → DBSET nodes

Every property of type `DbSet<T>` or `IDbSet<T>` is treated as a **DBSET** in the graph, for example:

```csharp
public class AppDbContext : DbContext
{
    public DbSet<Product> Products { get; set; }
}
```

produces a node similar to:

* `csharp:MyApp.Data.AppDbContext.Products|DBSET`

These DBSET nodes are the main link between the C# model and SQL tables.

### 1.3 `entityBaseTypes` → ENTITY nodes

For ORMs / codebases that use a common base class for entities, you can tell the indexer which base types represent “database entities” via:

```jsonc
"dbGraph": {
  "entityBaseTypes": [
    "Nop.Core.BaseEntity",
    "MyProject.Domain.EntityBase"
  ]
}
```

Any class that **inherits** from one of these types is treated as an **ENTITY** node, for example:

```csharp
public class Product : BaseEntity
{
    // ...
}
```

becomes roughly:

* `csharp:MyApp.Domain.Product|ENTITY`

DBSETs and migrations can then be connected to these ENTITY nodes and, where possible, to corresponding SQL `TABLE` nodes.

---

## 2. Inline SQL (`paths.inlineSqlRoot` + `inlineSql.extraHotMethods`)

### 2.1 Where inline SQL is scanned

`paths.inlineSqlRoot` should point to the C# code where raw SQL calls live (for example application services or repositories):

```jsonc
"paths": {
  "inlineSqlRoot": "D:/Repo/Application"
}
```

All `*.cs` files under this root are scanned for inline SQL.

### 2.2 Built-in hot methods

Some method names are treated as “always SQL” entry points. For these, the **first string argument** is analysed as SQL even if it is short or unusual.

Built-in hot methods:

* `SqlQuery`
* `ExecuteSql`
* `FromSql` (this also catches variants like `FromSqlRaw` / `FromSqlInterpolated` because the call text still contains `FromSql`)

Example:

```csharp
var items = context.Database.SqlQuery<Product>(
    "SELECT Id, Name FROM dbo.Product WHERE IsActive = 1");
```

The string passed into `SqlQuery(...)` is always treated as SQL and parsed.

### 2.3 Extending hot methods for other ORMs

If you use other ORMs or your own data-access helpers, you can extend the hot-method list in config:

```jsonc
"inlineSql": {
  "extraHotMethods": [
    "Query",
    "Execute",
    "RunRawSql"
  ]
}
```

Any invocation where the call expression text contains one of these tokens and the **first argument is a string literal** will be treated as inline SQL.

This is how you plug in calls such as:

```csharp
connection.Query<Customer>("SELECT ...");
sqlClient.Execute("UPDATE dbo.Customer SET ...");
```

### 2.4 SQL heuristics on string literals

In addition to hot methods, the scanner also looks at all string literals and applies simple heuristics:

* checks for typical SQL verbs: `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `EXEC`, `EXECUTE`
* checks for structure words: `FROM`, `WHERE`, `JOIN`, `INTO`, `TABLE`

If a string “looks like SQL” according to these rules, it is parsed and turned into inline-SQL artifacts, even if it is not passed into a known hot method.

---

## 3. Practical checklist

Minimal config for a typical EF-based app:

```jsonc
{
  "paths": {
    "solution":       "D:/Repo/MySolution.sln",
    "modelRoot":      "D:/Repo/DataAccess",
    "sqlRoot":        "D:/Repo/Sql",
    "inlineSqlRoot":  "D:/Repo/Application",
    "migrationsRoot": "D:/Repo/DataAccess/Migrations",
    "outRoot":        "D:/Artifacts/index",
    "tempRoot":       "D:/Work/IndexerTemp"
  },
  "dbGraph": {
    "entityBaseTypes": [
      "Nop.Core.BaseEntity"
    ]
  },
  "inlineSql": {
    "extraHotMethods": [
      "Query",
      "Execute",
      "RunRawSql"
    ]
  }
}
```

This setup gives you:

* SQL graph from `sqlRoot` (tables, views, procs, etc.),
* model / DbSet graph from `modelRoot` (DBSET / ENTITY nodes),
* inline SQL edges from `inlineSqlRoot` (METHOD/inline SQL → TABLE/VIEW),
* migration-based edges from `migrationsRoot` (MIGRATION → TABLE, SchemaChange/DataChange),
* MIGRATION bodies + structured summary in sql_bodies.jsonl from `migrationsRoot`
  (createsTables, dropsTables, addsColumns, dropsColumns, addsForeignKeys, dropsForeignKeys).
