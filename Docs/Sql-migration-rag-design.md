# SQL / Migration Graph Indexing – Design

## 1. Motivation & Goals

The goal of the **SQL / Migration** part of the indexer is to build a **single, unified dependency graph** for everything that touches the database:

- **SQL scripts** (project `.sql` files),
- **ORM / data-access code** (EF, LinqToDB, raw SQL),
- **C# migrations** (FluentMigrator, EF Migrations, custom migration frameworks).

This graph is later used by the RAG system to answer questions like:

- *“Which procedures write to table `X`?”*  
- *“Where is column `HasTierPrices` introduced or modified?”*  
- *“Which migrations touch table `Customer`?”*  
- *“Which C# methods read from or write to this table?”*

We do **not** try to fully re-construct the final schema by replaying all migrations.  
Instead, we:

> Treat each migration as a **node** and record **edges** from the migration to the DB objects it touches.

This is simpler, robust, and already extremely useful for analysis and RAG.

---

## 2. Out of Scope (for now)

Out-of-scope for the first iteration:

- No “virtual schema engine” that applies all migrations step-by-step to rebuild the final schema.
- No support for all possible migration DSLs and frameworks; we start with:
  - FluentMigrator-style APIs (e.g., `Create.Table`, `Create.TableFor<T>`, `Alter.Table`, etc.),
  - Data migrations using helper methods (e.g., `GetTable<T>`, `InsertEntity`, `UpdateEntity`, `DeleteEntity`).
- No full column-level history tracking across renames/drops (we can add this later).

---

## 3. Graph Model

All DB-related information is represented as a **graph**:

### 3.1 Node Types (kinds)

Key node kinds:

- `TABLE` – physical table (e.g., `dbo.Product`)
- `VIEW` – view
- `PROC` – stored procedure
- `FUNC` – scalar/table-valued function
- `TRIGGER` – trigger
- `TYPE` / `SEQUENCE` – other DB objects
- `DBSET` – ORM set (e.g., `DbSet<Product> Products`)
- `METHOD` – C# method that runs raw SQL
- `ENTITY` – C# entity class (optional, future)
- `MIGRATION` – C# migration (FluentMigrator, EF Migration, etc.)

Each node has at least:

- `key` – unique ID, e.g.:
  - `dbo.Product|TABLE`,  
  - `csharp:MyNamespace.Product|ENTITY`,  
  - `csharp:Nop.Data.Migrations.UpgradeTo480.SchemaMigration|MIGRATION`
- `kind` – one of the kinds above
- `name`, `schema`, `file`, `domain`, etc. (as we already do today)

### 3.2 Edge Types (relations)

Existing edge types:

- `ReadsFrom` – something reads from a TABLE/VIEW
- `WritesTo` – something writes to a TABLE
- `Executes` – a PROC/FUNC is executed
- `SynonymFor` – synonym mapping
- `On` – trigger attached to table
- `MapsTo` – mapping from code object to DB object (e.g. `DBSET → TABLE`)

**New edge types introduced by migrations:**

- `SchemaChange` – migration modifies the schema of a table/column/index.
- `DataChange` – migration modifies the data in a table (inserts, updates, deletes).

Examples:

- `MIGRATION: Nop.Data.Migrations.UpgradeTo480.SchemaMigration`  
  `--(SchemaChange)--> dbo.Product|TABLE`

- `MIGRATION: Nop.Data.Migrations.UpgradeTo480.DataMigration`  
  `--(DataChange)--> dbo.ActivityLogType|TABLE`

All of these edges end up in the same `sql_bundle/graph/{nodes.csv,edges.csv,graph.json}`.

---

## 4. Data Sources

We combine 3 main sources of information:

### 4.1 SQL scripts (.sql)

Handled by `BuildSqlKnowledge` in `LegacySqlIndexer`:

- Parses `.sql` files using `TSql150Parser`.
- Creates nodes for TABLE/PROC/VIEW/TRIGGER/etc.
- Creates edges `ReadsFrom`, `WritesTo`, `Executes`, etc.
- Writes:
  - `sql_bundle/docs/sql_bodies.jsonl` (full bodies),
  - `sql_bundle/graph/nodes.csv`,
  - `sql_bundle/graph/edges.csv`,
  - `sql_bundle/graph/graph.json`.

### 4.2 ORM / data-access code (EF, LinqToDB, raw SQL)

Handled by `AppendEfEdgesAndNodes`:

- Scans C# code for:
  - Entity mappings (`[Table]` attribute, `modelBuilder.Entity<T>().ToTable(...)`),
  - `DbSet<T>` or similar constructs,
  - raw SQL calls (`SqlQuery`, `ExecuteSql`, `FromSql`, etc.).
- Adds nodes for `DBSET` and certain `METHOD`s.
- Adds edges:
  - `DBSET → TABLE (MapsTo)`
  - `METHOD → TABLE/VIEW (ReadsFrom/WritesTo)` for raw SQL.

### 4.3 C# migrations (FluentMigrator, EF Migrations, custom)

**New component (this spec):**

- Scans C# projects for **migration classes** (e.g. classes inheriting from `*Migration`).
- Analyses the `Up()` method to detect:
  - **Schema changes** (create/alter/drop tables/indices/columns),
  - **Data changes** (insert/update/delete through helpers).
- Adds nodes:
  - `MIGRATION: Full.Namespace.ClassName|MIGRATION`.
- Adds edges:
  - `MIGRATION → TABLE (SchemaChange)`
  - `MIGRATION → TABLE (DataChange)`
  - (later: optional `MIGRATION → COLUMN / INDEX`, etc.)

---

## 5. Migration Adapter Design

### 5.1 Detection of migration classes

We treat a class as a migration if:

- It is a `class` in C#, and
- Its base type name ends with `"Migration"` (or matches a configurable pattern).

Example patterns:

- `SchemaMigration`, `DataMigration`, `ForwardOnlyMigration`, `SomeNamespace.SchemaMigration`.
- EF-style `Migration` classes.

Implementation sketch:

- For each C# syntax tree:
  - Find all `ClassDeclarationSyntax`.
  - Check `BaseList.Types` – if any `Type.ToString()` ends with `"Migration"`, we treat it as a migration.

### 5.2 Mapping migration to node

For each migration class:

- Compute full name: `Namespace.ClassName`.
- Define node key: `csharp:Full.Namespace.ClassName|MIGRATION`.
- Add a `NodeRow`:

```text
Key:     csharp:Nop.Data.Migrations.UpgradeTo480.SchemaMigration|MIGRATION
Kind:    MIGRATION
Name:    SchemaMigration
Schema:  csharp
File:    <cs file path>
Domain:  code
```

### 5.3 Analysing the `Up()` method

We focus on `Up()`:

- Find `MethodDeclarationSyntax` with:
  - Name: `Up`
  - No parameters

We then walk the body using a dedicated `MigrationBodyVisitor` (CSharpSyntaxWalker):

1. **Schema-level changes**

   Detect patterns like:

   - `Schema.Table("Product")...`
   - `Create.Table("Product")...`
   - `Alter.Table("Product")...`
   - `Create.Index("IX_Name").OnTable("Product")...`
   - `Delete.Column("X").FromTable("Product")`
   - `Create.TableFor<Product>()`

   For each table name resolved (literal string or based on `nameof(T)` / `TableFor<T>`):

   - Add edge:
     - `MIGRATION → dbo.Product|TABLE` with `relation = "SchemaChange"` and `toKind = "TABLE"`.

   For now, we default schema to `dbo` and allow SQL detection to refine later.

2. **Data-level changes**

   Detect patterns like:

   - `_dataProvider.GetTable<Product>()`
   - `_dataProvider.InsertEntity(new Product { ... })`
   - `_dataProvider.UpdateEntity(product)`
   - `_dataProvider.DeleteEntity(product)`

   Steps:

   - If we have `GetTable<T>()` or entity type is used in CRUD helper,
   - Extract entity type `T` (`Product`),
   - Map entity name to table name (simple case: same name; optional support for `NameCompatibilityManager` or configuration),
   - Add edge:
     - `MIGRATION → dbo.Product|TABLE` with `relation = "DataChange"`.

3. **Local name resolution**

   Support helper variables inside `Up()`:

   - `var productTableName = nameof(Product);`
   - `var columnName = "HasTierPrices";`

   The visitor builds a small map: `variableName -> logicalName`, so calls like `Schema.Table(productTableName)` still resolve to `Product`.

---

## 6. Configuration & Extensibility

We keep the core logic **generic**, and allow projects to plug in their specifics via configuration:

Possible config keys (example):

```jsonc
"dbGraph": {
  "entityBaseTypes": [
    "Nop.Core.BaseEntity",
    "MyProject.Domain.EntityBase"
  ],
  "migrationBaseNamePattern": "Migration",   // ends with "Migration"
  "migrationsRoot": "D:\\...\\Nop.Data"      // or leave empty to search under sqlRoot / efRoot
}
```

Usage:

- `entityBaseTypes` – optional, future: map ENTITY → TABLE (e.g. `Product : BaseEntity`).
- `migrationBaseNamePattern` – tells the migration adapter which base type/name suffix to treat as a migration.
- `migrationsRoot` – override for where to scan for migrations (e.g. `EfMigrations` folder or entire data project).

The aim: **RoslynIndexer.Core** implements the generic mechanisms; any project-specific behaviour is driven by **config**, not custom forks.

---

## 7. Integration with `LegacySqlIndexer`

Current flow in `LegacySqlIndexer.RunBuild`:

1. Build SQL knowledge → `BuildSqlKnowledge(...)`.
2. Auto-discover EF roots or use explicit `efRoot`.
3. Append EF edges → `AppendEfEdgesAndNodes(...)`.
4. Write graph + manifest.

We extend this to:

1. SQL part – unchanged.
2. EF part – unchanged (optional).
3. **Add migrations part**:

   ```csharp
   AppendEfEdgesAndNodes(codeRoots, outDir, nodes, edges);
   AppendEfMigrationEdgesAndNodes(codeRootsOrMigrationsRoot, outDir, nodes, edges);
   ```

   - `codeRootsOrMigrationsRoot` is either:
     - auto-discovered EF roots,
     - or explicit `paths.migrations` if configured (e.g. the `Nop.Data` project root).

4. Graph output stays the same: all nodes/edges are written together into `sql_bundle/graph/*`.

Result:  
**One graph** containing:

- Pure SQL objects,
- ORM/db-access mappings,
- Migrations as first-class nodes with schema/data-change edges.

---

## 8. Example Queries the Graph Should Support

Examples of questions we want the RAG + graph to answer:

1. **Who writes to table X?**

   - Use edges `WritesTo`, `DataChange`, `SchemaChange` outgoing to `TABLE: X`.

2. **In which migration was column Y introduced/modified?**

   - Use `SchemaChange` edges from `MIGRATION` to `TABLE: X`,  
   - Combine with body text / heuristics to locate specific column changes.

3. **Which migrations touch this table at all?**

   - Any `SchemaChange`/`DataChange` edge from a `MIGRATION` node to `TABLE: X`.

4. **Which parts of C# code interact with table X?**

   - Combine:
     - `DBSET → TABLE (MapsTo)`,
     - `METHOD → TABLE (ReadsFrom/WritesTo)`,
     - `MIGRATION → TABLE (SchemaChange/DataChange)`.

5. **What is the impact of changing/removing table X?**

   - Find all incoming edges to `TABLE: X` from:
     - SQL objects (procs, views, triggers),
     - C# nodes (DbSets, methods),
     - Migration nodes.

---

## 9. Future Work

Potential future extensions:

1. **Virtual schema reconstruction**

   - Apply all `SchemaChange` operations in migration order to build an approximate current schema (tables, columns, indexes).
   - Useful for answering “current state” questions even without a `.sqlproj` or live DB.

2. **Column-level, index-level graph nodes**

   - Nodes for columns and indexes (e.g. `dbo.Product.HasTierPrices|COLUMN`).
   - Edges from migrations specifying exactly which columns/indexes are affected.

3. **Multi-framework support**

   - Additional adapters for:
     - EF Core migrations,
     - DbUp,
     - Home-grown migration frameworks (via configuration and simple patterns).

---

**Summary**

- We do **not** try to replay migrations to rebuild the schema (for now).
- We **do** treat migrations as first-class nodes in the same graph as SQL and ORM.
- Migrations add **SchemaChange** and **DataChange** edges to tables (and later columns/indexes).
- The design is **generic**, not nopCommerce-specific: nopCommerce is just the first serious testbed.
- All DB-related knowledge remains in **one unified graph**, consumed by the RAG system.
