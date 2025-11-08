# 🧪 RoslynIndexer.Tests

## Purpose

This project contains **integration and functional tests** for the modern code indexer **RoslynIndexer.Net9**.
The goal is to verify that the entire pipeline — from MSBuild evaluation to artifact generation (`chunks.json`, `deps.json`, `meta.json`) — works correctly end-to-end.

---

## 🧭 Architectural Decision

### ✅ Testing Focus: **.NET 9 / Core**

All new tests are executed **only** against the `RoslynIndexer.Net9` project.
This version is the **source of truth** and the main target for new functionality and regression tests.

### 💤 Legacy Runner: **RoslynIndexer.Net48**

Remains **for backward compatibility only**:

* Used for manual execution on older Windows servers (.NET Framework 4.8),
* Not covered by automated tests,
* Verified occasionally through manual smoke tests.

> 🧩 **Note:** The shared library **RoslynIndexer.Core** targets **.NET Standard**, ensuring cross‑platform and cross‑runtime compatibility.
>
> **Testing coverage:** A significant portion of functionality is implemented in **RoslynIndexer.Core**, which is fully covered by tests.
> This ensures that most improvements and fixes automatically benefit both `.NET 9` and `.NET 4.8` users, maintaining functional parity and avoiding regressions. **Note:** A significant portion of functionality is implemented in **RoslynIndexer.Core**, which is fully covered by tests.


---

## 📁 Test Structure

| Folder           | Purpose                                                           |
| ---------------- | ----------------------------------------------------------------- |
| `BasicCSharp/`   | Tests for general C# code indexing (classes, methods, interfaces) |
| `EfCodeFirst/`   | Tests for EF Code First models, migrations, and `.tt` templates   |
| `SqlScripts/`    | Tests for parsing `.sql` files                                    |
| `MixedIndexing/` | Tests for projects containing both C# and SQL                     |
| `Common/`        | Shared utilities and helpers                                      |

Each group includes its own sample data under a `Samples/` subfolder.

---

## 🧱 Naming Conventions

| Type          | Class Name                  | Example Method                                    |
| ------------- | --------------------------- | ------------------------------------------------- |
| General C#    | `BasicCSharp_IndexingTests` | `Should_Index_CSharp_Code_And_Create_Artifacts()` |
| EF Code First | `EfCodeFirst_IndexingTests` | `Should_Index_DbContext_And_Migrations()`         |
| SQL           | `SqlScripts_IndexingTests`  | `Should_Parse_Sql_Files_Into_Graph()`             |

---

## 🧩 Test Scope

1. **Create a temporary repository** — minimal `.csproj` and `.sln` files generated dynamically.
2. **Run the indexing pipeline** — `IndexingPipeline`, `MsBuildWorkspaceLoader`.
3. **Validate artifacts** — verify existence and integrity of `chunks.json`, `dependencies.json`, and `meta.json`.
4. **Analyze results** — check for valid class/method counts and consistent dependency graphs.

---

## 🚀 Running Tests

Run tests from Visual Studio or the CLI:

```bash
dotnet test RoslynIndexer.Tests
```

Temporary directories are automatically created and cleaned up (`%TEMP%` on Windows, `/tmp` on WSL/Linux).

---

## ⚖️ Maintenance Rules

* Every new feature in `RoslynIndexer.Net9` must include a corresponding integration test here.
* No new tests are added for `.NET 4.8`; only occasional manual verification if necessary.
* Tests must remain **deterministic, offline, and self-contained** — no network access or external dependencies.

---

## 📜 Decision Log

| Date           | Decision                                                                                                                                         |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| **2025-11-06** | All integration tests target **.NET 9 (Core)** only. The `.NET 4.8` runner remains as a legacy compatibility layer without active test coverage. |

---

> ℹ️ Licensing: This test project is covered by the **MIT License** defined at the solution root.
