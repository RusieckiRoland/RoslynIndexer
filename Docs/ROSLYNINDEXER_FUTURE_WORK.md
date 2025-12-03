# RoslynIndexer – Future Work Notes

*Last updated: 2025-12-01*

This file is **not a task list**. The project is considered **feature-complete for current RAG needs**.
These notes only exist so that “future me” (and the assistant) can quickly remember what could be improved
if there is a strong reason to invest more time.

---

## 0. Current baseline (what already works)

At the time of writing:

* The indexer produces a **single unified graph per run**:

  * `sql_code_bundle/graph/nodes.csv`
  * `sql_code_bundle/graph/edges.csv`
  * `sql_code_bundle/graph/graph.json`
  * plus bodies: `sql_bodies.jsonl`, `docs/bodies/*`, `manifest.json`.
* Supported sources:

  * **SQL files** – tables, FK constraints, etc. → TABLE nodes + TABLE→TABLE (ForeignKey) edges.
  * **Inline SQL** – simple JOIN/WHERE patterns → TABLE→TABLE dependency edges.
  * **EF model** – DbSet<T>, entity base types, Fluent API (`HasOne/HasMany + HasForeignKey`) →
    ENTITY/DBSET nodes, ENTITY→TABLE MapsTo edges, and TABLE→TABLE (ForeignKey) edges.
  * **EF migrations (v2)** – wiring is present, core support is there, but not fully exploited
    for cross-source FK/schema comparison.
* There are end-to-end tests covering:

  * SQL FK extraction.
  * Inline SQL edges.
  * EF Fluent FK in multiple variants (HasOne/HasMany, generic and lambda forms).

This baseline is **good enough for RAG** over .NET + SQL for the original use case.

---

## 1. Short-term ideas (if I ever come back "for a bit")

These are **small, high-ROI improvements** that can be done without reopening the project as a big epics.
If I come back for a few hours or a weekend, this is the first place to look.

### 1.1 ForeignKey source tagging and deduplication

**Problem / opportunity**:
The same ForeignKey relationship can be discovered from different sources:

* SQL schema (CREATE/ALTER TABLE)
* EF Fluent API
* EF migrations
* inline SQL analysis

Currently each source may emit its own edge. For analysis and RAG it is often more useful to know that
“this FK is confirmed by SQL and EF” instead of having multiple separate edges.

**Idea**:

* Extend `EdgeRow` / `edges.csv` (or add a parallel JSONL) with a **SourceFlags** concept, e.g.:

  * `sql`, `ef`, `migrations`, `inline`
  * or a bitmask / string list.
* Introduce a single helper, e.g. `AddForeignKeyEdge(fromTableKey, toTableKey, source, ...)`, that:

  * normalizes TABLE keys (`schema.table|TABLE`),
  * deduplicates edges by `(From, To, Relation="ForeignKey")`,
  * merges SourceFlags when the same FK comes from additional sources.

**Why this matters**:

* RAG and tools can quickly see where FK is defined (only SQL, only EF, both, etc.).
* This is the foundation for consistency checks and "drift" detection later.

### 1.2 One full "4 sources" end-to-end test

**Goal**:
Prove in a single automated test that all four sources can be active in one run and contribute to the same graph.

**Idea**:

* Create a very small synthetic solution with:

  * 1–2 SQL files defining tables + FK.
  * 1 EF DbContext + entities + Fluent API mirroring the same schema.
  * 1–2 EF migrations (Up only is enough) that create the same tables/FKs.
  * 1–2 inline SQL snippets that reference these tables.
* Add an E2E test that:

  * runs the indexer with all roots configured (`sqlRoot`, `modelRoot`, `migrationsRoot`, `inlineSqlRoot`),
  * asserts that:

    * nodes from all four domains appear in `nodes.csv`,
    * at least one ForeignKey edge is present,
    * optionally: that the FK edge is tagged with more than one source when SourceFlags exist.

**Why this matters**:

* It is a single, strong safety net across the whole pipeline.
* Good reference for future debugging and for documentation / blog posts.

### 1.3 Minimal "How to use with RAG" note

**Goal**:
Make it easy for "future me" to remember how to use the artifacts in a RAG pipeline, without re-learning
everything from scratch.

**Idea**:

* Add a small Markdown file (e.g. `docs/RAG_Quickstart.md`) with:

  * one or two concrete RAG scenarios (e.g. "explain all dependencies between service X and table Y"),
  * example of how to:

    * load `nodes.csv/edges.csv` in a script,
    * select a subgraph,
    * turn it into text chunks for the vector store.

**Why this matters**:

* In a few months it will be much easier to plug RoslynIndexer into a new RAG stack
  without reverse-engineering the format again.

---

## 2. Medium-term ideas (if the product feels worth real investment)

These items are **bigger**. They only make sense if RoslynIndexer starts to feel like a core product
for consulting / on-prem AI for .NET + SQL.

### 2.1 Schema consistency report (SQL vs EF vs migrations)

**Goal**:
Given all sources, detect simple mismatches between them and surface them as a separate artifact.

**Examples**:

* FK present in SQL but missing in EF.
* FK present only in EF.
* Tables present only in SQL, only in EF, only in migrations.

**Idea**:

* After building the graph, run a post-processing step that:

  * groups tables and FKs by logical identity (schema + name, or normalized key),
  * classifies each into categories such as `OnlySql`, `OnlyEf`, `SqlAndEf`, etc.
* Emit the result as a JSON/JSONL report under something like `sql_code_bundle/reports/schema_drift.json`.

**Why this matters**:

* This turns the indexer into a simple schema-audit tool.
* RAG and other tools can answer questions like: "where does my EF model disagree with the database?".

### 2.2 Richer EF + migrations schema extraction

**Goal**:
Make EF and migrations more than just sources of FK edges and table names.

**Idea**:

* From EF and migrations, extract:

  * table and column definitions (where reasonably reliable),
  * FK and index definitions,
  * basic metadata (nullable, key columns) when clearly derivable.
* Feed this into the same node/edge model used for SQL, so that schema can be understood
  even when SQL files are incomplete.

**Why this matters**:

* Some projects keep schema knowledge mostly in EF/migrations, not in raw .sql.
* Improves coverage of "real world" solutions.

### 2.3 Inline SQL – slightly deeper parsing

**Goal**:
Capture more realistic table relationships from inline SQL without going into heavy, fragile heuristics.

**Idea**:

* Extend the inline SQL scanner to handle:

  * LEFT/RIGHT JOIN patterns,
  * simple subqueries,
  * basic INSERT/UPDATE/DELETE where the target table is unambiguous.
* Keep it conservative: prefer fewer, trustworthy edges over noisy guesses.

**Why this matters**:

* Many legacy systems hide important access paths in inline SQL.
* Even partial coverage can significantly help RAG and impact analysis.

---

## 3. Long-term ideas (only if the project becomes strategic)

These items are "nice to have" and **not required** for the original RAG goal.
They only make sense if RoslynIndexer becomes a key piece of tooling.

### 3.1 Richer edge metadata

**Goal**:
Make edges more expressive without breaking the simple CSV format.

**Idea**:

* Keep `edges.csv` as a lightweight index.
* Add a parallel JSONL (e.g. `edges_meta.jsonl`) with optional metadata per edge:

  * for ForeignKey: list of columns on both sides,
  * for Calls/Uses: hints about direction, call type, etc.

### 3.2 Small graph query helper / API

**Goal**:
Make it easier for tools and RAG pipelines to query the graph programmatically.

**Idea**:

* Provide a small library or script that:

  * loads `nodes.csv` and `edges.csv`,
  * supports a few basic operations: neighbors, paths, subgraphs around a node.
* Optionally expose this as a local HTTP service for other tools.

### 3.3 Ready-made RAG scenarios and examples

**Goal**:
Ship RoslynIndexer with a few concrete, documented RAG use cases.

**Examples**:

* "Show the chain of FKs from table A to table B and the code that touches each hop."
* "Find all code paths that eventually write to table T."
* "Explain how data flows from API controller X to table Y."

**Idea**:

* Provide example notebooks / scripts that:

  * select relevant parts of the graph,
  * collect code/SQL bodies,
  * feed them into a vector store and demonstrate a working RAG query.

---

## 4. Reminder to future me

* Right now the project is **good enough for the intended RAG scenario**.
* None of the items above are mandatory. They are just a menu of possible future improvements.
* If the project still does what you need: it is perfectly OK to leave it as-is and not touch it again.
