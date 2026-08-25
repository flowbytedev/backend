# AI Features — Data Section

Proposed AI capabilities for the **data section** of the FlowByte platform. Each idea is grounded in
infrastructure that already exists in this repo:

- **Azure OpenAI** via `IChatService` (configured from the `AzureOpenAI` appsettings section; in-memory
  chat history via `InMemoryChatMessageRepository`; local Ollama fallback under `ExternalChatbot`).
- **Analytical query engines** — DuckDB (`IDuckdbService`) and ClickHouse (`IClickHouseService`).
- **Datasets & ingestion** — external-database ingestion (`IngestionService`, `IngestionJob`) plus dataset
  storage and column metadata.
- **Metrics** — dynamic SQL filters using `;` as a multi-value separator (generates `IN(...)`); filter
  types `text`, `date`, `number`, `select`.
- **Scheduler** — Hangfire recurring jobs (`Application.Scheduler`) with queue-based deployment.
- **Email microservice** — React Email / Resend service for outbound notifications.
- **Real-time** — SignalR hubs (`/realtime/salesdata`, `/notification/datajob`).

Features are ordered by effort. Each entry describes **what it does**, **how it maps to existing code**,
**a sketch of the approach**, and **risks / guardrails**.

---

## Tier 1 — Quick wins (reuse `IChatService` + existing schema/query engines)

### 1. Natural-language → SQL ("Ask your data") ⭐ Anchor feature

**What it does**
A chat box attached to a dataset (or the data section generally) where a user types a question in plain
English — *"top 10 items by net sales last week per store"* — and the system generates the appropriate
DuckDB/ClickHouse SQL, executes it, and returns a results grid plus an optional auto-chart.

**Why it's the anchor**
It is the single highest-value capability for a BI product: it collapses the gap between "I have a question"
and "I have an answer" for non-technical users, and it reuses every major piece of data infrastructure you
already have (schema metadata, query engines, chat service).

**How it maps to existing code**
- Schema/column metadata for the dataset → injected as context for the model.
- Generated SQL executed through `IDuckdbService` / `IClickHouseService` (whichever backs the dataset).
- Conversation managed through `IChatService` + the in-memory chat repository.
- Multi-tenancy: every generated query must be scoped by `CompanyId` (see the `X-Company-Id` convention).

**Approach sketch**
1. Build a **schema prompt**: table name(s), column names, types, and (if available) human descriptions —
   see feature #5, which makes this dramatically more accurate.
2. Ask the model to emit **a single `SELECT` statement only**, plus a short rationale.
3. **Validate before executing**:
   - Parse/whitelist: reject anything that isn't a single `SELECT` (no `INSERT`/`UPDATE`/`DELETE`/`DROP`,
     no multiple statements, no comments hiding a second statement).
   - Inject a mandatory `company_id = @companyId` predicate (don't trust the model to add it).
   - Enforce a hard `LIMIT` (e.g. clamp to 5000 rows, mirroring the `Math.Clamp(limit, 1, 5000)` pattern
     already used in the Daily Inventory controller).
   - Run with a read-only connection / query timeout.
4. Return columns + rows to the existing grid component; offer a "show SQL" toggle for transparency.
5. On error, feed the DB error back to the model for a self-correction retry (bounded to 1–2 attempts).

**Risks / guardrails**
- **SQL injection / destructive queries** — the validation + read-only connection + forced company predicate
  is the security boundary, not the prompt. Never execute model output unvalidated.
- **Hallucinated columns** — constrain strictly to the provided schema; reject unknown identifiers.
- **Cost / latency** — cache schema prompts; consider a smaller/faster model tier for query generation.

---

### 2. Auto-generated metric & filter suggestions

**What it does**
Given a dataset's columns, the model proposes a starter set of **metrics** (sums, counts, ratios,
time-series aggregates) and **sensible default filters**, turning a raw ingested table into a usable
dashboard in one click.

**How it maps to existing code**
- Reads the dataset's column metadata.
- Emits metric definitions in your existing metric model shape, and filters using the `;`-separated
  multi-value convention with the correct filter type (`text` / `date` / `number` / `select`).

**Approach sketch**
1. Feed columns + inferred types (and descriptions from #5) to the model.
2. Ask for N candidate metrics, each with: display name, aggregation, group-by dimension, and a default
   filter set expressed in your filter syntax.
3. Present them as **suggestions the user accepts/edits** — do not auto-create silently.
4. Persist accepted suggestions through the normal metric-creation path.

**Risks / guardrails**
- Treat output as **suggestions**, always human-confirmed before persistence.
- Validate that referenced columns exist and aggregation/type pairings are valid (no `SUM` on a text column).

---

### 3. Chart / insight narration

**What it does**
For any rendered metric or chart, generate a one-paragraph plain-English summary — *"Revenue rose 12%
week-over-week, driven mostly by Store X; the Beverages category was the only decliner."*

**How it maps to existing code**
- Operates on data **already computed** for the chart — no new query path needed.
- Pure `IChatService` call with the series/aggregates as context.

**Approach sketch**
1. After a metric/chart renders, pass the underlying aggregated data (not raw rows) + the chart's
   title/dimensions to the model.
2. Request a concise summary (2–4 sentences) highlighting trend, top contributors, and notable outliers.
3. Render below the chart; cache by (metric id + data hash) so it isn't regenerated on every view.

**Risks / guardrails**
- **Don't send raw row-level data** if it contains PII — summarize from aggregates.
- Cheap and low-risk overall; main concern is avoiding overstated/causal claims ("driven by") when the data
  only shows correlation — prompt the model to describe, not infer causation.

---

## Tier 2 — Medium effort (some new plumbing)

### 4. Automated anomaly detection on sales / inventory

**What it does**
A scheduled job scans recent sales and inventory data for outliers — sudden drops, stockouts, unusual
spikes — and uses the model to turn each detection into a human-readable alert delivered by email (and/or a
real-time SignalR notification).

**How it maps to existing code**
- Runs as a **Hangfire recurring job** in `Application.Scheduler` (same pattern as `SalesSnapshotEmailJob` /
  `AssetPingJob`), routed to the appropriate queue.
- Detection reads sales (real-time pipeline) and Daily Inventory data.
- Alerts go out through the existing **email microservice**, mirroring `SalesSnapshotEmail` /
  `IncidentNotificationEmail` config; optionally push to `/realtime/salesdata` or `/notification/datajob`.

**Approach sketch**
1. **Statistics first, AI second**: compute the anomaly numerically (z-score / rolling-window deviation /
   stockout threshold). Do **not** ask the model to "find anomalies" in raw data — it's unreliable and
   expensive.
2. For each flagged anomaly, ask the model to write a short, business-friendly explanation and suggested
   next step using the surrounding context (which store, which item, magnitude, recent trend).
3. Batch into a digest email per company; respect the per-company tenancy boundary.

**Risks / guardrails**
- Keep the **detection deterministic**; AI only narrates. This controls cost and avoids false confidence.
- Alert fatigue — add thresholds/dedup so the same anomaly isn't re-sent each run.

---

### 5. Dataset documentation / semantic layer

**What it does**
On (or after) ingestion, auto-generate **column descriptions**, detect likely **PII**, infer **types/units**,
and propose a friendly **display name** for each dataset and column. Stored as metadata.

**Why it matters beyond docs**
This semantic layer is the **accuracy multiplier** for features #1, #2, and #3 — the model writes far better
SQL and narration when columns are described ("`net_amt_acy` = net sales amount in accounting currency")
rather than guessed from cryptic names.

**How it maps to existing code**
- Hooks into the ingestion flow (`IngestionService` / `IngestionJob`) as a post-ingest step, or runs
  on-demand from the dataset UI.
- Samples a few rows + column names through `IChatService`; persists results as dataset/column metadata.

**Approach sketch**
1. Sample N rows per column (sanitized) + the column name.
2. Ask the model for: description, semantic type/unit, and a PII likelihood flag.
3. Store as editable metadata — users can correct, and corrections feed back into query accuracy.

**Risks / guardrails**
- **Don't ship raw sensitive samples** to the model without masking; sample conservatively.
- PII detection is a **hint**, not a compliance control — surface it for human review.

---

### 6. Ingestion error explainer

**What it does**
When an ingestion run fails, the model converts the raw exception + context into a plain-language cause and
a suggested fix, written into the run log — turning cryptic Hangfire/EF errors into something a
non-developer can act on. (Directly motivated by the recent debugging of *"No connection is configured on
the source database entity"* and the `TypeLoadException` stale-deploy issues.)

**How it maps to existing code**
- Wraps the failure path in `IngestionService` / `IngestionJob`; the explanation is written to the
  Hangfire console log (`PerformContext.WriteLine`) alongside the existing run output.
- Has access to run context: dataset name, source entity id, company id, the exception message/stack.

**Approach sketch**
1. On a caught ingestion failure, assemble: error message, exception type, dataset/source identifiers,
   and the step that failed (fetch/transform/load).
2. Ask the model for a short "likely cause + suggested fix" — primed with the common known causes
   (missing/mismatched connection row, company-id mismatch, stale deployment, auth/secret decryption).
3. Append to the run log; optionally include in the failure notification email.

**Risks / guardrails**
- **Never echo secrets** (connection strings, passwords, client secrets) into the log or model prompt —
  scrub them first.
- The explanation is advisory; keep the original raw error in the log too.

---

## Tier 3 — Bigger bets

### 7. Conversational dashboard builder

**What it does**
Extends feature #1 into a **multi-turn agent** that builds and edits an entire dashboard through
conversation: *"show me sales by region"* → *"add a stock-on-hand column"* → *"filter to this brand"* →
*"make that a bar chart."* Each turn adds or modifies a metric/chart.

**How it maps to existing code**
- Builds on NL→SQL (#1) and metric suggestions (#2).
- Renders into your existing **Blazor WASM + Fluent UI** metric/chart components.
- Maintains conversation + dashboard state across turns (`IChatService` history + a dashboard state model).

**Approach sketch**
1. Maintain a structured representation of the dashboard (list of metric/chart specs).
2. Each user turn → model produces a **diff/operation** against that spec (add chart, add column, change
   filter), not a from-scratch rebuild.
3. Apply the operation, re-render, and confirm back to the user in natural language.

**Risks / guardrails**
- State drift across turns — keep an explicit, inspectable dashboard spec rather than relying on the model's
  memory.
- All the SQL-safety guardrails from #1 apply to every generated query.

---

### 8. Forecasting / demand prediction (Daily Inventory)

**What it does**
For the Daily Inventory feature, predict next-period demand per item-location from the 6-week rolling sales
already computed, flag likely **stockouts**, and suggest **reorder quantities**.

**How it maps to existing code**
- Consumes the 6-week `sumIf` rolling sales + stock-on-hand the Daily Inventory query already produces.
- Could run as a scheduled Hangfire job writing predictions back, or computed on-demand for the view.

**Approach sketch**
1. **Forecast with a statistical/ML method** (moving average, exponential smoothing, or a simple regression)
   per item-location — this is the reliable core, not an LLM job.
2. Compare forecast demand vs. stock-on-hand + lead time → stockout risk + suggested reorder qty.
3. Use the model on top for the **narration/justification** ("reorder ~120 units; demand trending up 8%/wk,
   current cover is 4 days").

**Risks / guardrails**
- Keep the **forecast numeric and explainable**; use AI for explanation, not the prediction itself.
- Validate against history before trusting; surface confidence, don't auto-order.

---

## Suggested sequencing

A practical path that produces a visible, demoable AI story quickly while building toward the larger bets:

1. **#3 Insight narration** — cheap, sits on existing rendered data, immediately visible.
2. **#1 NL→SQL** — the anchor; the centerpiece capability.
3. **#5 Semantic layer** — quietly makes #1, #2, #3 much more accurate.
4. **#2 Metric suggestions** — rounds out the "raw data → dashboard" loop.
5. **#6 Error explainer** & **#4 Anomaly detection** — operational value, leverage the scheduler + email.
6. **#7 Conversational builder** & **#8 Forecasting** — bigger investments once the foundation is proven.

## Cross-cutting guardrails (apply to all)

- **Tenancy**: every AI-driven query/operation must be scoped by `CompanyId` — enforced in code, not by the
  prompt.
- **Read-only by default**: generated SQL executes against read-only connections with timeouts and row caps.
- **Secrets**: never include connection strings, passwords, API keys, or PII samples in model prompts or logs.
- **Human-in-the-loop**: AI output that mutates state (metrics, dashboards, reorders) is a *suggestion* until
  a user confirms.
- **Determinism where it counts**: anomaly detection and forecasting are numeric/statistical; AI narrates,
  it doesn't decide.
