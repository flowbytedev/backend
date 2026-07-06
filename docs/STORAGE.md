# DuckDB Dataset Storage — Options & Trade-offs

How to store the DuckDB dataset files for the FlowByte platform, considering some tables contain
**millions of records** and the app is deployed across **two servers** (a local on-prem sales server and a
cloud server) with **Hangfire-driven ingestion**.

## The fact that drives everything

**DuckDB is an embedded analytical engine designed for local, random-access file I/O with file locking.**
Azure Blob is *object* storage — no in-place writes, no partial updates, no file locks; you replace whole
objects. So "move the DuckDB files to blob" is not a drop-in swap — it forces a specific architecture.
The options below reflect that reality.

Relevant existing config:
- `Duckdb:DuckdbFilePath` — current local file location.
- `AzureBlob` appsettings section — already present (credentials available for blob options).
- Two servers + Hangfire ingestion jobs (single-writer concurrency matters).

---

## Option A — Local disk (current setup)

Store `.duckdb` files on the deployment server's filesystem (`Duckdb:DuckdbFilePath`). This is DuckDB's
intended design.

### Features
- Memory-mapped, random-access local I/O.
- Full read/write: `INSERT`/`UPDATE`/`DELETE`, transactions, in-place page writes.
- Native single-writer file locking.

### Pros
- **Maximum performance** — fastest option for multi-million-row queries; no network hop.
- **Simplicity** — one file path, no extensions/auth/orchestration.
- **Full mutation support** — ingestion jobs can freely modify datasets.
- **Correct locking** — concurrent Hangfire jobs are coordinated safely (one writer at a time per file).
- **No egress/transaction cost or per-query latency.**

### Cons
- **No durability** — disk failure or lost VM = data gone (object storage gives 11-nines durability; local disk does not). *Biggest drawback.*
- **No backup unless added** — a bad ingestion run, accidental delete, or corruption is unrecoverable without a backup.
- **Single-server only** — the two-server setup can't share datasets; each box sees only its own files.
- **Capacity bounded by VM disk** — must size/monitor; resizing is more friction than blob.
- **Scaling/HA limits** — can't run multiple app instances sharing the same files (DuckDB single-writer; shared mounts reintroduce locking problems).
- **File locking blocks redeploys** — the running process holds the file open; stop/start ordering matters (same class of issue as the DLL-lock on deploy).

### Best for
A single deployed app instance with analytical/reporting datasets where raw query speed matters. **Sound
default** — but pair it with **Option B** to close the durability gap.

---

## Option B — Local disk + scheduled blob backup (recommended baseline)

Keep files local for performance (Option A), and add a **Hangfire job that copies the `.duckdb` files to
Azure Blob on a schedule** (e.g. nightly). Blob is a safety net, not the live store.

### Features
- Local working store + periodic full-file copy to blob.
- Reuses existing `AzureBlob` config; slots next to other scheduler jobs.
- Optional blob versioning/snapshots for point-in-time recovery.

### Pros
- **Local speed retained** — DuckDB stays on its happy path.
- **Durability + recovery path** — survives disk/VM loss; restore from the last backup.
- **Low complexity** — a simple copy job; no live blob I/O on the query path.
- **No concurrency or whole-file-transfer issues during normal operation** (transfer happens once per backup window, off the hot path).

### Cons
- **Recovery is point-in-time** — you lose changes since the last backup (RPO = backup interval).
- **Backup transfers the whole file** — large `.duckdb` files mean large nightly uploads (acceptable off-hours; still a cost).
- **Doesn't solve multi-server sharing or HA** — still effectively single-writer/single-node for live data.
- **Restore is manual** — download + replace; some downtime on recovery.

### Best for
**The pragmatic default for almost everyone here.** Keeps Option A's performance and closes its biggest gap
(durability) with minimal complexity.

---

## Option C — `.duckdb` file stored *in* blob (download → use → upload)

Store the whole `.duckdb` file as a blob object and treat blob as the source of truth: download the full
file to local disk before querying, mutate locally, upload the full file back.

> DuckDB cannot open a blob path live for read/write — there is no in-place page writing to object storage.
> This option always means whole-file round-trips.

### Features
- Blob is the canonical store; local disk is a transient working copy.
- Blob versioning/snapshots available.
- Multiple servers can pull the same file.

### Pros
- **Durability & central storage** — survives server loss; one source of truth.
- **Multi-server access** — both servers can fetch the same file.
- **Free backup/versioning** via blob snapshots.

### Cons (severe at millions of records)
- **Whole-file transfer every time** — a multi-GB file must be fully downloaded before any query and fully re-uploaded after any write; no "write just the changed pages." Appending 10k rows still re-uploads the whole DB.
- **No concurrency / locking** — two jobs that download→edit→upload silently overwrite each other (last-write-wins, lost data). **Dangerous with concurrent Hangfire ingestion.**
- **Latency on every query** — first query pays the full download cost; no in-place querying.
- **Local disk still required** — you don't escape needing local storage for the working copy.
- **Egress + transaction cost** — repeatedly moving GB-sized files adds up.

### Best for
Small, infrequently-written datasets where central/multi-server access matters more than performance.
**Not suitable for large or frequently-ingested datasets** — i.e. avoid for the "millions of records" case.

---

## Option D — Data as Parquet in blob, queried via DuckDB (data-lake pattern)

Store the dataset *data* as **Parquet files in blob**, and query them directly with DuckDB's `azure`/`httpfs`
extension using **HTTP range requests** (reads only the row groups/columns a query needs). This is the
idiomatic pattern for large analytical data on object storage.

### Features
- Columnar Parquet with predicate/projection **pushdown**.
- Range reads — pull only needed columns/row groups, not the whole file.
- **Partitioning** (e.g. by date and/or `CompanyId`) so queries skip whole files.
- Uses existing `AzureBlob` config + DuckDB azure extension.

### Pros
- **Designed for scale** — a query over millions of rows reads only relevant data, not the entire dataset.
- **No whole-file transfer** — efficient range reads.
- **Cheap, durable, central** — all blob benefits without DuckDB fighting the storage model.
- **Partition pruning** — great for time-series sales data; aligns with the per-`CompanyId` tenancy model.
- **Multi-server friendly** — both servers read the same blob data (read-only) without locking conflicts.

### Cons
- **Effectively read-only / append-by-file** — no in-place `UPDATE`/`DELETE`; you write new files or rewrite partitions. Fine for analytical/reporting datasets, not transactional mutation.
- **Network latency per query** vs. local disk — mitigated by caching + partition pruning, but a cold remote query is slower than local.
- **More moving parts** — ingestion must write Parquet partitions; you manage a small data-lake layout instead of one file.
- **Extension/auth setup** — DuckDB azure extension + credentials (incremental, given existing `AzureBlob` config).

### Best for
Large analytical datasets (millions of rows), multi-server read access, and time-series data that partitions
naturally. **The right path when local-only genuinely breaks down.**

---

## Option E — Network/mounted filesystem (Azure Files / blobfuse) — caution

Mount blob or a file share as a filesystem path and point `Duckdb:DuckdbFilePath` at it.

### Features
- Looks like a local path to DuckDB; minimal code change.
- Central storage accessible from multiple servers.

### Pros
- **Apparent simplicity** — no download/upload code; just a different path.
- **Central + durable** storage.

### Cons
- **Unreliable file locking** — network filesystems often don't honor the locking semantics DuckDB needs; risks corruption with concurrent access. **This is the core problem.**
- **Poor random-access performance** — DuckDB's page-level random I/O over a network mount is slow vs. local disk, especially for large files.
- **Reintroduces the multi-writer hazard** — sharing one file across servers/instances is exactly what DuckDB's single-writer model forbids.

### Best for
Generally **not recommended** for live DuckDB databases. Acceptable only for low-concurrency, read-mostly
scenarios — and even then Option D is usually a better object-storage answer.

---

## Side-by-side summary

| Option | Query speed | Durability | Multi-server | Large-table (millions) | Write/mutate | Complexity |
|---|---|---|---|---|---|---|
| **A. Local disk** | Best | None | No | Good (local) | Full | Lowest |
| **B. Local + blob backup** | Best | Good (RPO = interval) | No | Good (local) | Full | Low |
| **C. `.duckdb` in blob** | Poor (full DL/UL) | High | Yes (with conflict risk) | Bad | Full but last-write-wins | High |
| **D. Parquet in blob** | Good (range reads) | High | Yes (read) | Excellent | Append-by-file | Medium |
| **E. Network mount** | Poor | High | Yes (unsafe locking) | Poor | Risky | Medium |

## Recommendation

1. **Default:** **Option B** — keep files local for speed, add a scheduled blob backup for durability. Best
   effort-to-value ratio; closes Option A's only serious gap.
2. **For the large, multi-million-row datasets and/or shared-across-servers needs:** **Option D**
   (Parquet in blob) — built for scale, no whole-file transfers, multi-server read access, partition pruning
   that aligns with the `CompanyId` tenancy model. Keep small/hot datasets local.
3. **Avoid** **Option C** for large or frequently-ingested datasets (whole-file transfers + last-write-wins
   concurrency), and **Option E** for live databases (locking/performance hazards) given the two-server +
   concurrent-Hangfire-ingestion setup.

A common end state: **hybrid** — Option B for small/hot datasets, Option D for the large analytical ones.
