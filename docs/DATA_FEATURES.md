# Data Management — Feature Roadmap

Ideas for extending the **Data** module, grounded in what the platform already does. Each item
notes *why it matters*, *how it fits this codebase*, a rough *effort* (S / M / L), and what it
*depends on*. Use the [prioritized roadmap](#prioritized-roadmap) at the bottom to pick a starting point.

> Effort key: **S** ≈ days · **M** ≈ 1–2 weeks · **L** ≈ multi-week / cross-cutting.

---

## Where we are today (baseline)

So suggestions don't duplicate existing work, here's the current capability set:

- **Datasets & tables** — full CRUD; each dataset is its own DuckDB file (`{datasetId}.duckdb`); metadata in `ApplicationDbContext` (schema via `Application.Database.Backend` DACPAC).
- **Ingestion** — 3-step CSV import wizard with **AI schema inference**; bulk `COPY` into DuckDB; an external anonymous import endpoint.
- **Viewing** — paged / filtered / sorted data viewer with column selection and CSV download (full + filtered).
- **Collaboration** — per-table comments with @mentions; dataset sharing (`DatasetUser`) with email notifications.
- **External access** — scoped **API keys** (per dataset/table, read or import) + contextual API docs.
- **Other** — real-time sales dashboard (SignalR), database-entity table discovery, a separate Metrics module.

Everything below is *not* yet built.

---

## 1. Governance, catalog & quality

### 1.1 Data dictionary / catalog — `M`
Per-column business metadata: description, tags, **PII/sensitivity flag**, owner, source-of-truth notes, unit/format. Surface it in the viewer (column tooltips) and a searchable catalog page.
- **Fits:** extend `Column` + a new `column_metadata` table (DACPAC); render in `ViewData.razor` and `ListTable`.
- **Why:** as table count grows, "what does this column mean / can I share it?" becomes the #1 friction. Feeds row/column security (1.4) and export governance.
- **Depends on:** nothing.

### 1.2 Data quality rules & scoring — `L`
Declarative per-column rules (not-null, unique, regex, numeric range, allowed-value set, freshness). Run on import and/or on a schedule; produce a **quality score** + failing-row report per table.
- **Fits:** DuckDB can evaluate every rule as a `SELECT COUNT(*) WHERE NOT (...)`; store rules + results in new tables; schedule via the existing **Hangfire** scheduler.
- **Why:** catches bad data before it reaches consumers/API keys.
- **Depends on:** import-validation hooks (3.1) for the inline path.

### 1.3 Data profiling — `S`
One-click per-column stats: row count, null %, distinct count, min/max, top values, simple histogram.
- **Fits:** DuckDB `SUMMARIZE`/aggregate queries are near-instant; add a "Profile" tab to the data viewer.
- **Why:** highest value-to-effort ratio here — instant insight, reuses the DuckDB connection.

### 1.4 Row-level & column-level security — `L`
Restrict which **rows** (e.g. by region/branch) and **columns** (mask PII) a user/role/API key can see. Define policies per table; enforce in `DuckdbService.QueryTableDataAsync` and `ExternalDataController`.
- **Fits:** inject predicate/`SELECT`-list rewriting into the query builder (already centralized via `BuildWhereClause`).
- **Why:** unlocks safely sharing one table with many audiences; pairs with the PII flags in 1.1.
- **Depends on:** 1.1 (to know which columns are sensitive).

---

## 2. Versioning, history & lifecycle

### 2.1 Import / table versioning & snapshots — `M`
Keep a snapshot per import (DuckDB makes copying a table cheap), **diff** versions (rows added/changed), and **roll back**.
- **Fits:** version metadata table + snapshot tables/files; "Versions" tab in the viewer.
- **Why:** "the numbers changed yesterday — what changed?" and safe re-imports.

### 2.2 Audit log — `M`
Record who did what: imports, edits, deletes, downloads, API-key data pulls. Filterable per dataset/table/user.
- **Fits:** an `audit_event` table + a small write helper called from the data controllers; the API-key path already stamps `LastUsedAt` (extend to per-request logging — see 4.2).
- **Why:** compliance, debugging, and trust.

### 2.3 Soft delete, recycle bin & retention — `S`–`M`
Deletes go to a recoverable state; auto-purge after N days; per-dataset retention policy.
- **Fits:** the project already uses `DeleteBehavior.Restrict` (no cascade) and explicit cleanup — add `deleted_at` + a Hangfire purge job.
- **Why:** accidental table deletion is currently unrecoverable.

---

## 3. Ingestion (extend the import pipeline)

### 3.1 Import validation & staging preview — `M`
Validate a file against the table schema **before** committing: type-cast checks, required columns, row-level error report; choose to reject the file or quarantine bad rows.
- **Fits:** load into a temp DuckDB table, validate, then promote — slots between the upload and `COPY` steps of the existing wizard.
- **Why:** today a malformed CSV either fails opaquely or lands dirty data.

### 3.2 Import modes: append / replace / upsert — `M`
Currently import is append-only. Add **replace** (truncate+load) and **upsert** (dedupe on a primary key).
- **Fits:** extend `ImportCsvDataAsync`; DuckDB supports `INSERT ... ON CONFLICT` / `MERGE`-style patterns.
- **Why:** re-importing a corrected file shouldn't duplicate rows.
- **Depends on:** a primary-key concept per table (small schema add).

### 3.3 More file formats — `S` each
Excel (`.xlsx`), JSON, Parquet, TSV. DuckDB reads Parquet/JSON natively; Excel needs a parse step.
- **Why:** CSV-only is a frequent blocker for business users.

### 3.4 Scheduled / automated ingestion — `L`
Pull data on a cron from external databases, REST APIs, or blob/SFTP — with incremental loads.
- **Fits:** the **Hangfire** scheduler + the existing **database-entity discovery** (MSSQL/PG/MySQL/ClickHouse/DuckDB connectors) are most of the plumbing.
- **Why:** turns the platform from manual-upload into a real data hub.

---

## 4. Sharing, delivery & external access (extend API keys)

### 4.1 Scheduled exports / data delivery — `M`
Push a table (or a saved query/filtered view) as CSV/Parquet to **email, Azure Blob, SFTP, or a webhook** on a schedule.
- **Fits:** Hangfire + existing email helper + `AzureBlob` config; reuse the download/CSV logic.
- **Why:** partners often want data pushed, not pulled.

### 4.2 API-key usage analytics & quotas — `S`–`M`
Per-key request log, last-N calls, rows served, and optional **rate limits / monthly quotas**.
- **Fits:** direct extension of the new API-key feature (`ApiKeyService` already validates per request); add a usage table + a counter check in `ApiKeyAuthenticationHandler`.
- **Why:** visibility and abuse protection for external consumers.

### 4.3 Public read-only views / embeds — `M`
Publish a specific filtered view as a tokenized read-only page or embeddable widget.
- **Depends on:** 1.4 (so a public view can't leak restricted columns).

---

## 5. Query & analysis

### 5.1 SQL query workbench + saved queries — `M`
Run ad-hoc **DuckDB SQL** against a dataset, save queries, share them, and promote a query to a reusable **view**.
- **Fits:** DuckDB executes directly; add an editor page + a `saved_query` table. Gate writes behind `EDIT_DATA`; keep external execution read-only.
- **Why:** power users currently can't ask anything the viewer's filters don't express.

### 5.2 No-code transformations & computed columns — `M`
Add calculated columns and simple transforms (split, concat, cast, map values) without SQL.
- **Depends on:** 5.1 under the hood (transforms compile to SQL).

### 5.3 In-viewer aggregation, pivot & charts — `M`
Group-by + summary aggregates and lightweight charts inside the data viewer (distinct from the heavier Metrics module).
- **Fits:** DuckDB aggregates + the existing FluentUI components.

### 5.4 Cross-table joins / relationships — `L`
Define relationships between tables and query/join them in the viewer or workbench.
- **Depends on:** 5.1 and a relationship model (overlaps with lineage, 6.1).

---

## 6. Discovery & organization

### 6.1 Lineage & relationships graph — `M`
Visualize where data comes from and what depends on it (imports → tables → saved views → exports/API keys).
- **Fits:** the platform already does Power BI lineage and database discovery — reuse that graph pattern for the Data module.

### 6.2 Tags, folders, favorites & global search — `S`–`M`
Organize datasets/tables with tags and folders; star favorites; search across names, descriptions, and catalog metadata.
- **Why:** essential once there are dozens of datasets.
- **Depends on:** 1.1 for richer search.

### 6.3 Bulk operations & dataset cloning/templates — `S`
Multi-select for delete/export/share; clone a dataset's structure as a template.

---

## 7. Notifications & collaboration

### 7.1 Subscriptions & alerts — `M`
Notify (in-app + email) on: table updated, import failed, quality score dropped, schema changed.
- **Fits:** the existing **SignalR `NotificationHub`** + email infrastructure.

### 7.2 Approval workflows — `M`
Require review/approval before a table is published or shared externally (e.g. flagged-PII tables).
- **Depends on:** 1.1 / 1.4.

---

## Prioritized roadmap

### Quick wins (high value, low effort) — start here
| Feature | Effort | Why first |
|---|---|---|
| 1.3 Data profiling | S | Near-zero cost on DuckDB, immediate insight |
| 4.2 API-key usage analytics & quotas | S–M | Direct extension of the new API-key feature |
| 2.3 Soft delete & recycle bin | S–M | Removes a real "oops, it's gone" risk |
| 6.2 Tags / folders / search | S–M | Scales the UI as datasets multiply |
| 3.3 More file formats (Parquet/JSON/Excel) | S | Unblocks non-CSV users |

### Next (clear value, moderate effort)
| Feature | Effort |
|---|---|
| 1.1 Data dictionary / catalog | M |
| 3.1 Import validation & staging preview | M |
| 3.2 Import modes (append/replace/upsert) | M |
| 5.1 SQL query workbench + saved queries | M |
| 2.2 Audit log | M |
| 4.1 Scheduled exports / delivery | M |

### Strategic bets (high value, larger / cross-cutting)
| Feature | Effort |
|---|---|
| 1.4 Row & column-level security | L |
| 1.2 Data quality rules & scoring | L |
| 3.4 Scheduled / automated ingestion | L |
| 2.1 Versioning & snapshots | M–L |
| 6.1 Lineage graph | M |

---

## Suggested first sprint

A coherent, shippable slice that reuses the DuckDB + Hangfire + API-key foundations:

1. **Data profiling** (1.3) — instant column stats in the viewer.
2. **Import validation & staging preview** (3.1) + **upsert/replace modes** (3.2) — make ingestion trustworthy and repeatable.
3. **API-key usage analytics** (4.2) — close the loop on the feature just shipped.

Together these touch ingestion, viewing, and external access without requiring the larger schema/security work, and each delivers value independently.
