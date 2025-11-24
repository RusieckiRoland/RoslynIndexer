# RoslynIndexer

High‑signal, zero‑build **code indexer** for large .NET repositories. It evaluates MSBuild projects (without compiling), walks the source tree, extracts C# code + dependency graphs, and optionally builds a SQL/ORM graph — all exported as portable bundles for RAG systems.

This runner targets **.NET 9** (`RoslynIndexer.Net9`). A legacy .NET Framework 4.8 variant also exists.

---

## 🚀 Features

* **Zero-build indexing** — uses MSBuild project *evaluation*, not compilation.
* **C# extraction** — classes, methods, code bodies → `regular_code_bundle/`.
* **SQL + ORM graph** (optional) — T‑SQL, EF entities, inline SQL → `sql_code_bundle/`.
* **Optional migration graph** — detects C# migrations if `migrationsRoot` is provided.
* **Portable output** — everything packaged into a ZIP named after the current Git branch.
* **Robust** — unknown project types (`*.sqlproj`, `*.vcxproj`, `*.rptproj`) are skipped gracefully.

---

## ⚙️ Requirements

* Windows 10/11 (x64)
* .NET SDK **9.0**
* Visual Studio 2022 / Build Tools with MSBuild components
* (Optional) Web Publishing tasks if your solution uses `TransformXml`

The indexer **does not run or build** your code — it only reads and evaluates projects.

---

## 🏁 Quick Start

```powershell
# Recommended: use JSON config
 dotnet run --project .\RoslynIndexer.Net9\RoslynIndexer.Net9.csproj -- --config D:\Config\indexer.json

# Minimal (no JSON)
 dotnet run --project .\RoslynIndexer.Net9\RoslynIndexer.Net9.csproj -- \
    --solution D:\Repo\MySolution.sln \
    --temp-root D:\Work\IndexerTemp
```

---

## 📄 Configuration (JSON)

All configuration lives under `paths`, `msbuild`, `externalTasks`, and optional modules.

### Example

```jsonc
{
  "paths": {
    "solution": "D:/Repo/MySolution.sln",
    "modelRoot": "D:/Repo/DataAccess",
    "sqlRoot": "D:/Repo/Sql",
    "inlineSqlRoot": "D:/Repo/Application",
    "migrationsRoot": "D:/Repo/DataAccess/Migrations",

    "outRoot": "D:/Artifacts/index",
    "tempRoot": "D:/Work/IndexerTemp"
  },

  "msbuild": {
    "VisualStudioVersion": "17.0",
    "MSBuildExtensionsPath32": "C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild",
    "VSToolsPath": "C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Microsoft/VisualStudio/v17.0"
  },

  "externalTasks": {
    "TransformXml": "C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Microsoft/VisualStudio/v17.0/Web/Microsoft.Web.Publishing.Tasks.dll"
  },

  "dbGraph": {
    "entityBaseTypes": ["Nop.Core.BaseEntity"]
  },

  "inlineSql": {
    "extraHotMethods": ["Query", "Execute", "RunRawSql"]
  }
}
```

### Folder semantics

| Path key         | What is scanned                                                                         |
| ---------------- | --------------------------------------------------------------------------------------- |
| `solution`       | Entire .sln → all C# code; produces regular code bundle + dependency graph              |
| `modelRoot`      | ORM model (EF entities, DbSet<T>, base entities configured via `entityBaseTypes`)       |
| `sqlRoot`        | Raw `.sql` scripts (tables, procs, functions, triggers)                                 |
| `inlineSqlRoot`  | C# code containing inline SQL (EF `FromSql`, `ExecuteSql`, plus configured hot methods) |
| `migrationsRoot` | Migration classes (`*Migration`), parsed for SchemaChange/DataChange edges              |

---

## 📦 Output

```
<branch>.zip
├── regular_code_bundle/
│   ├── chunks.json
│   ├── dependencies.json
│   └── README_WSL.txt
├── sql_code_bundle/
│   ├── graph/
│   │   ├── nodes.csv
│   │   ├── edges.csv
│   │   └── graph.json
│   ├── docs/
│   │   └── bodies/
│   │       ├── sql_bodies.jsonl
│   │       └── *.sql
│   └── repo_meta.json
```

ZIP name = current Git branch. If a ZIP exists, a timestamp suffix is added.

---

## 🔧 How it works (pipeline)

1. Register MSBuild (Visual Studio instance detection)
2. Repository scan + hashing
3. MSBuildWorkspace evaluation (no compilation)
4. C# extraction → chunks + dependency graph
5. SQL/EF/inline SQL/migration graph
6. Bundle write + ZIP

---

## 🐞 Troubleshooting

### TransformXml not found

Add correct DLL path under `externalTasks.TransformXml`.

### Legacy rulesets errors

Disable code analysis:

```jsonc
{"msbuild": { "RunCodeAnalysis": "false", "CodeAnalysisRuleSet": "" }}
```

### SQL graph empty

* Ensure `sqlRoot` contains `.sql` files
* Ensure `modelRoot` points to EF model if ORM mapping expected

### File write errors

Check permissions for `outRoot` and `tempRoot`.


---

## 📜 License

MIT
