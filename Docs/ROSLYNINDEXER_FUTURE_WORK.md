# RoslynIndexer – Configuration Understanding Module (Draft Proposal)
**Last updated:** 2025-12-06  
**Priority:** **Highest — required for next major RAG milestones**

---

## 0. Motivation

Modern .NET systems increasingly store critical application logic **not only in source code**, but also in **configuration**.  
Configuration may define:

- data shapes,  
- feature switches,  
- dynamic UI fields,  
- content models (as in Orchard),  
- routing rules,  
- workflow behaviors,  
- tenant-specific overrides.

A RAG system that understands **only** C#, SQL, and inline SQL will see **only part of the truth** of such systems.

Examples already identified:

- Orchard Core – Content Types, Fields, and Parts defined entirely in configuration JSON stored in the database.  
- Enterprise systems – dynamic forms stored in configuration tables.  
- Microservices – YAML/JSON defining endpoints, bindings, schemas.

**None of this is visible to RoslynIndexer today.**

Therefore a new **Configuration Understanding Module** is required.

---

## 1. The Problem (Why Configuration Matters)

### 1.1 Configuration exists in many formats
- JSON  
- YAML  
- XML  
- Relational configuration tables  
- Custom formats  
- Orchard-style runtime JSON documents

### 1.2 It does not map directly to source code
A field may appear on a form only because configuration defines it.  
No class or property exists in C# to represent it.

### 1.3 RAG cannot explain system behavior without configuration
Key questions become unanswerable:

- “Where does this field come from?”  
- “Which features are enabled for this tenant?”  
- “How is this content type structured?”  
- “Why does module X behave differently in environment Y?”

**Source code alone cannot answer these.**

---

## 2. Goal: A Unified Configuration Representation

Although configuration appears in many shapes, the RAG pipeline should see it as **one consistent, normalized structure**.

Example normalized fragment:

```json
{
  "kind": "config",
  "source": "json",
  "path": "src/App/contenttypes/blogpost.json",
  "data": {
    "ContentType": "BlogPost",
    "Fields": {
      "Title": { "type": "TextField" },
      "Body": { "type": "HtmlField" }
    }
  }
}
```

Principles:

- Normalize all configuration sources into **key → value → metadata** form.  
- Preserve **origin**, **path**, and **raw structure**.  
- Store configuration in a **dedicated bundle**, separate from SQL and C#.

---

## 3. Initial Test Case: Orchard Core

Orchard is chosen as the first target because:

- It defines its data model through configuration, not code.  
- Content Types, Parts, and Fields have **no matching C# classes**.  
- Stored document shapes are dynamic and not reflected in source code.

Critical reality:

> If a field appears on screen because it exists in Orchard’s configuration,  
> **RoslynIndexer currently cannot determine where it came from.**

The Configuration Module directly addresses this gap.

---

## 4. Architecture Proposal: `configuration_bundle/`

New output directory, alongside existing ones:

- `regular_code_bundle/`  
- `sql_code_bundle/`  
- **`configuration_bundle/` ← NEW**

### 4.1 Proposed layout

```
configuration_bundle/
    manifest.json
    configs.jsonl            # normalized configuration fragments
    config_graph.json        # optional: relationships between config items
    raw/                     # (optional) raw or lightly cleaned config files
```

### 4.2 Extraction pipeline

1. **Detect configuration sources**  
   - JSON/YAML/XML files  
   - SQL configuration tables  
   - Orchard tenant exports (future)

2. **Normalize structure**  
   - keys, values, metadata  
   - include dependency hints  
   - preserve hierarchical paths

3. **Expose configuration to RAG**  
   - NOT via FAISS  
   - via a dedicated retrieval step (`retrieve_config`)

### 4.3 Why configuration must NOT go to FAISS

- Configuration is structured data, not semantic text.  
- Embedding it would pollute and degrade FAISS retrieval.  
- It must be treated as a **first-class data source**, not as text.

---

## 5. RAG Integration

Example pipeline step:

```yaml
- retrieve_config:
    query: "${user_query}"
```

The model receives:

- semantic code context from FAISS,  
- structural dependencies via the graph,  
- configuration fragments via the configuration bundle.

Prompt example:

```
Use configuration + code + dependency graph to answer the question.
If configuration overrides code behavior, prefer configuration.
```

---

## 6. Future Extensions

Once the Configuration Module exists, additional capabilities become possible:

- configuration diff between environments/tenants,  
- configuration → code dependency mapping,  
- “why does this field exist?” explanation,  
- runtime behavior reconstruction,  
- impact analysis for configuration changes.

---

## 7. Summary

**Configuration has become a first-class source of truth in modern systems.  
RoslynIndexer must evolve to understand it.**

This requires:

- a new bundle: `configuration_bundle/`,  
- extractors for multiple config formats,  
- normalization into a unified representation,  
- explicit retrieval in the RAG pipeline,  
- Orchard Core as the first real-world case.

This work is designated as **highest priority** for enabling full-system understanding in the RAG platform.
