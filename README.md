# RoslynIndexer

High‑signal, zero‑build **code indexer** for large .NET repos. It evaluates MSBuild projects (without compiling), walks the source tree, extracts C# code chunks + dependency graph, and optionally builds a SQL/EF graph — all into portable artifacts you can zip or feed into RAG.

---

## Features

- **No full builds** – uses `MSBuildWorkspace` for project *evaluation* only.
- **C# extraction** – classes/methods/bodies → `regular_code_bundle/` with metadata and deps.
- **SQL graph (optional)** – scans T‑SQL (+EF model when provided) → `sql_bundle/graph/` (`nodes.csv`, `edges.csv`, `graph.json`).
- **Portable artifacts** – zip created from `tempRoot`, named after the Git branch.
- **Robust to missing SDKs** – unsupported project types (`*.rptproj`, `*.vcxproj`, `*.sqlproj`) are logged and skipped.

> This README covers the **.NET 9 runner** (`RoslynIndexer.Net9`). A legacy **.NET Framework 4.8 runner** exists for special cases.

---

## Requirements

- Windows 10/11 (x64).
- **.NET SDK 9.0** (for the .NET 9 runner).
- **Visual Studio 2022** (Community/Professional/Enterprise **or** Build Tools) with MSBuild components.
  - If your solution uses **web transforms** (SlowCheetah/`TransformXml`), install **Web Publishing tasks** (see *Troubleshooting › TransformXml*).

> The indexer **does not build** your solution and **does not run** code. It only evaluates MSBuild, parses, and writes artifacts.

---

## Quick Start

```powershell
# Run with a JSON config (recommended)
dotnet run --project .\RoslynIndexer.Net9\RoslynIndexer.Net9.csproj -- --config D:\W\config.json

# Or minimal CLI (no JSON)
dotnet run --project .\RoslynIndexer.Net9\RoslynIndexer.Net9.csproj -- `
  --solution D:\Repo\src\MySolution.sln `
  --temp-root D:\Work\.idx
```

Common flags:

- `--config <path>` – JSON config file (see below).

---

## Configuration (JSON)

Keep everything in one file. **Recommended structure:**

```jsonc
{
  // Repository-specific paths (prefer relative paths when possible)
  "paths": {
    "solution": "C:\\Repo\\src\\MySolution.sln",
    "sql": null,                                  // optional
    "ef":  "C:\\Repo\\src\\Server\\DataAccess",   // optional (see EF/SQL section)
    "out": ".\\.artifacts\\index",
    "tempRoot": ".\\.tmp\\index"
  },

  // Optional: MSBuild property overrides — ONLY if your repo needs them.
  // The runner reads *exactly* these values; it does not guess paths.
  "msbuild": {
    "VisualStudioVersion": "17.0",
    "MSBuildExtensionsPath32": "C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild",
    "VSToolsPath": "C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Microsoft\\VisualStudio\\v17.0"
  },

  // Optional: task assemblies MSBuild may expect at build-time.
  // The runner registers them so projects can be *evaluated*.
  "externalTasks": {
    "TransformXml": "C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Microsoft\\VisualStudio\\v17.0\\Web\\Microsoft.Web.Publishing.Tasks.dll"
  }
}
```

### Key naming (important)

- Inside `paths` use **camelCase**: `tempRoot` ✅ (not `temp-root` ❌).
- On the **CLI**, use dashes: `--temp-root` ✅.

The runner accepts both top-level keys and `paths.*` overrides; `paths.*` wins when present.

---

## EF / SQL paths

- `paths.sql` — directory with T‑SQL scripts (scanned recursively). If omitted, the SQL graph step is skipped.
- `paths.ef` — **one** root directory for your **EF Code‑First model**. The scan is recursive, so all `Migrations` folders under that root are discovered **automatically**. You do **not** provide a separate migrations path.

Example:

```jsonc
{
  "paths": {
    "solution": "D:\\Repo\\src\\MySolution.sln",
    "sql": "D:\\Repo\\src\\Server\\Databases\\MainDb",
    "ef":  "D:\\Repo\\src\\Server\\DataAccess",
    "tempRoot": "D:\\Work\\.idx",
    "out": "\\\\wsl.localhost\\Ubuntu-22.04\\home\\user\\repo\\branches"
  }
}
```

---

## What the indexer produces


```
<branch>.zip  # e.g., master.zip – the final archive
├── regular_code_bundle/
│   ├── chunks.json          # Code chunks with metadata (paths, line numbers) for embedding/AI search
│   ├── dependencies.json    # Dependency graph (adjacency lists for projects, classes, methods)
│   └── README_WSL.txt       # Optional instructions or notes for WSL/Linux environments
├── sql_bundle/              # Only generated if SQL path is provided
│   ├── graph/
│   │   ├── edges.csv        # Edges of the SQL dependency graph (relations between objects like tables/FKs)
│   │   ├── graph.json       # Full SQL graph in JSON (nodes + edges) for visualization or loading into graph DBs
│   │   └── nodes.csv        # Nodes of the graph (SQL entities: tables, views, procedures with attributes)
│   ├── docs/
│   │   └── bodies/
│   │       ├── sql_bodies.jsonl  # JSON lines with full SQL script bodies (each line: {path, content}) for bulk RAG import
│   │       └── [*.sql files]     # Raw SQL scripts (e.g., Applications.Activity.TABLE.sql) for manual review or execution
│   └── repo_meta.json       # Repository metadata (e.g., branch, timestamp, paths, exclusions) for context and tracking
```

### Archive (ZIP)

- ZIP name = **Git branch name** (e.g., `master.zip`).
- Contents have **no** top-level folder; files start at archive root.
- If `paths.out` is set, the ZIP is **copied** there (UNC/WSL paths supported).
- If a ZIP with the same name exists in `out`, a timestamp is appended:  
  `stable_YYYYMMDD_HHmmss.zip`.

---

## How it works (high level)

1. **MSBuild registration** – selects a VS instance via `MSBuildLocator`.
2. **Scan + hash** – traverses repo, computes SHA‑256s, collects file metadata.
3. **MSBuild evaluation** – opens the solution in `MSBuildWorkspace` (no compile).
4. **C# extraction** – pulls code items and cross‑refs, emits chunks + deps.
5. **SQL/EF graph (optional)** – builds nodes/edges from T‑SQL and EF (if `ef` provided).
6. **Artifact write + ZIP** – writes bundles and archives `tempRoot`.

---

## CLI reference

```
--config <path>          Use JSON config (preferred)
--solution <path>        Path to .sln (required if no config)
--temp-root <path>       Working folder (required if no config)
--sql <path>             Root of SQL scripts (optional)
--ef <path>              EF model/migrations root (optional)
--inline-sql <path>      Additional SQL snippets folder (optional)
--quiet                  Reduce output to errors only
--log error              Same as --quiet
```

---

## Troubleshooting

### 1) **TransformXml**: task cannot be loaded / path not found

**Symptom**
```
Task "TransformXml" could not be loaded ... The system cannot find the path specified.
```
**Fix**
1. Verify the DLL exists (Community path below):
   ```
   C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VisualStudio\v17.0\Web\Microsoft.Web.Publishing.Tasks.dll
   ```
2. Put that exact path under `externalTasks.TransformXml` in your JSON.
3. Ensure there is **no duplication** in the path (e.g., `...\\Microsoft\\Microsoft\\VisualStudio...` is wrong).

### 2) Legacy rulesets missing (e.g., `ManagedMinimumRules.ruleset`)

Some projects reference old Code Analysis rulesets. Either install VS components that ship them, or disable during evaluation:

```jsonc
{
  "msbuild": {
    "RunCodeAnalysis": "false",
    "CodeAnalysisRuleSet": ""
  }
}
```

### 3) Unknown project type: `.sqlproj`, `.rptproj`, `.vcxproj`

This is **not fatal**. The indexer logs and skips them. C# extraction continues.

### 4) `tempRoot` not picked up / key name mismatch

- In JSON use **`paths.tempRoot`**.
- On CLI use `--temp-root`.

### 5) .NET 4.8 runner: `Microsoft.NET.StringTools` / `Microsoft.IO.Redist` binding issues

If you see `Method not found ... Microsoft.IO.Path.GetFileName(ReadOnlySpan<char>)` or `FileNotFoundException: Microsoft.NET.StringTools`:
- Ensure the runner registers MSBuild **early** and resolves `Microsoft.Build*` and `Microsoft.NET.StringTools` from the VS MSBuild folder.
- Do **not** forcibly redirect `Microsoft.IO.Redist`; let the runtime pick the compatible one installed with VS.
- In JSON, set `msbuild.MSBuildExtensionsPath32` to the **MSBuild root** (e.g., `...\\MSBuild`), *not* to `...\\Current\\Bin`.

### 6) SQL graph does nothing

Checklist:
- `paths.sql` points to a folder that actually contains `.sql` files.
- If you want EF types, set `paths.ef` to your EF root; migrations are discovered recursively.

### 7) Access denied / cannot write artifacts

Ensure write permissions to `paths.out` and `paths.tempRoot`. Exclude them from antivirus if necessary.


---

## Tips for CI agents

- Install **VS 2022 Build Tools** + the workloads required by your repo.
- Pre-provision task DLLs and reference them via `externalTasks`.
- Keep the JSON in the **repo root**. Use relative `paths` so the same file works locally and in CI.

---

## Safety & Privacy

- The indexer **does not run** your code and **does not access the internet**.
- Only files under the configured paths are read. Artifacts are written locally.

---

## License

MIT.

---

## Support

Open an issue in your repository and attach:
- the full command you ran,
- your JSON config,
- the tail of the log with the error.
