# Changelog

## [0.2.0] - 2025-12-03

### Added
- Support for `.slnx` solution filters in Net9/Net48 runners:
  - `MsBuildWorkspaceLoader` counts real projects instead of Project(...) lines.
  - E2E tests for `.slnx` happy path and EF-only `.slnx` scenarios.
- Inline SQL scanning and graph integration:
  - `InlineSqlScanner` detects SQL literals (heuristics + ScriptDom + hot methods).
  - C# methods using SQL now have `METHOD` nodes and `ReadsFrom` edges in the graph.
  - New E2E suite covering inline SQL, LooksLikeSql heuristics and extraHotMethods on/off.
- Foreign key edges in the SQL/EF graph from all layers:
  - `.sql` scripts: `CREATE TABLE` / `ALTER TABLE` with FK constraints now produce
    `TABLE -> TABLE` `ForeignKey` edges.
  - Inline SQL DDL: inline `CREATE/ALTER TABLE ... FOREIGN KEY ... REFERENCES ...`
    in C# produces FK edges between child and parent tables.
  - EF Fluent API: `modelBuilder.Entity<T>().HasOne/HasMany(...).HasForeignKey(...)`
    chains generate `TABLE -> TABLE` `ForeignKey` edges (always Child → Parent),
    with coverage for generic and lambda-based patterns.
  - EF migrations: `AddForeignKey(...)` operations produce `TABLE -> TABLE`
    `ForeignKey` edges based on the migration model.
- Bodies and metadata in `sql_bodies.jsonl`:
  - Inline SQL bodies for methods detected by `InlineSqlScanner`
    (built-in hot methods + `inlineSql.extraHotMethods`).
  - POCO `ENTITY` bodies for classes discovered via `DbSet<T>/IDbSet<T>` and
    configured `dbGraph.entityBaseTypes`.
  - Migration `Up()` bodies plus structured summaries of schema changes
    (created/dropped tables, column add/drop/rename, FKs), with E2E coverage.
- Entity detection driven by `dbGraph.entityBaseTypes`:
  - Global `DbGraphConfig` (`GlobalDbGraphConfig`) used to create `ENTITY` nodes
    for base-type-derived POCOs and optional `MapsTo` edges to TABLE nodes
    (entityMap, `[Table]` attribute, existing SQL tables).
- E2E diagnostics:
  - `RI_KEEP_E2E=1` flag and helpers to preserve temp roots for inspection
    (MiniEf solutions, `sql_code_bundle` artifacts).

### Changed
- SQL/EF/migration graph pipeline:
  - Clear separation and documentation of roots:
    `solution`, `sqlRoot`, `modelRoot`, `inlineSqlRoot`, `migrationsRoot`, `outRoot`, `tempRoot`.
  - Refined EF/Migration stages (`AppendEfEdgesAndNodes`, `AppendEfMigrationEdgesAndNodes`)
    to use `GlobalEfMigrationRoots` with backward-compatible fallbacks.
  - Unified logging across stages (`[IN]`, `[SQL]`, `[EF]`, `[MIGRATIONS]`).
- Runners and bootstraps:
  - Net9 and Net48 runners now share a config-first bootstrap:
    `config.json` is the single source of truth for paths and `dbGraph` settings.
  - Both runners use the same pipeline:
    Scan + Hash + MSBuild + `SqlEfGraphRunner` + Git + chunks + ZIP,
    with consistent `sql_code_bundle` layout for graph/docs/sql_bodies.
  - EF-only mode is supported in both Net9 and Net48 (no SQL root required).
- Inline SQL integration:
  - SQL/EF/inline analysis moved into the core pipeline; inline roots and
    `extraHotMethods` are read from config and forwarded via global settings.
  - Default EF hot methods (SqlQuery/ExecuteSql/FromSql) are always treated as SQL;
    other literals still go through LooksLikeSql heuristics.
- Dependencies and metadata:
  - EF migrations can be analyzed even without a configured SQL scripts path.
  - Chunk metadata now stores relative file paths for better traceability.
  - NuGet dependencies aligned and Newtonsoft.Json bumped to 13.0.4.
- Documentation:
  - Mention `migrationsRoot` as a source of MIGRATION bodies.
  - Added/updated configuration overview docs describing how each root
    contributes to the graph.

### Fixed
- Stabilised E2E scenarios by making `outRoot` resolution and `sql_code_bundle`
  output layout deterministic.
- Ensured EF-only `.slnx` scenarios always produce at least one SQL body in
  `sql_bodies.jsonl`.
- Cleaned up confusing SQL/EF logging (no more ambiguous “Done ?” messages).

---

## [0.1.0] - 2025-11-08

### Added
- Initial import of RoslynIndexer Core and Net9/Net48 runners.
- Basic SQL/EF graph generation (`nodes.csv`, `edges.csv`, `graph.json`,
  `sql_bodies.jsonl`, `docs/bodies/*`, `manifest.json`).
- First SQL/EF end-to-end tests for simple solutions.
