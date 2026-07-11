# Query Notebook — Feature Roadmap

Ideas for extending the Query Notebook feature (`Application.Client/Pages/Data/QueryNotebook.razor`,
`QueryNotebookService`, `QueryNotebooksController`). Already shipped: SQL/Markdown cells, cross-cell and
cross-dataset references, `RunAll` with topological ordering, Open in Table View, Create Ingestion Job
from a cell, an AI-assist that suggests SQL per cell, cell templates/snippets, drag-to-reorder, table- and
column-level SQL autocomplete, a dependency graph view, chart cells (bar/line/pie), and CSV/Excel export
of a cell's result.

## Execution & scheduling

- ~~Recurring notebook runs via Hangfire.~~ Shipped — `NotebookRunJob` (`[Queue("notebook")]`) in
  `Application.Shared`, reconciled against `query_notebook`'s new `cron_expression`/`schedule_enabled`
  columns by `NotebookRunRegistrarJob` in `Application.Scheduler` (mirrors `IngestionRegistrarJob`). Runs
  execute with `isAdmin: true` (the schedule's own authorization happened when the owner turned it on — a
  cron trigger has no "acting user"). The scheduler process can't resolve a share's email (no
  `UserManagementDbContext`/Identity wired there), so `INotebookSharingService` is a
  `UnsupportedNotebookSharingService` stub there — safe only because a scheduled run's permission check
  short-circuits on `isAdmin` before ever reaching it. Added `"notebook"` to `Hangfire:Queues` alongside
  `"default"` in `Application.Scheduler/appsettings.json` — **any other deployed appsettings for a
  default-queue-owning server needs the same addition, or scheduled notebook jobs enqueue and never run.**
  UI: a schedule button (cron + timezone) in the notebook editor header.
- ~~Cancel a running cell or an in-progress `RunAll`.~~ Shipped — `INotebookRunCancellationRegistry`
  (singleton, web app only) tracks a linked `CancellationTokenSource` per in-flight run, keyed
  `cell:{cellId}` / `notebook:{id}:all`. `POST .../cancel` endpoints trigger it. UI: the Run/Run All button
  becomes Cancel while running.
- ~~Parameterized cells (`{{start_date}}`).~~ Shipped — no schema change; `{{name}}` placeholders are
  detected client-side (regex) to decide whether to prompt, and substituted server-side into a per-run copy
  of the SQL (the stored cell text never changes). An unfilled/mismatched placeholder is left as-is so it
  fails loudly at the SQL parser instead of silently becoming an empty string.
- ~~Run history/timeline per cell.~~ Shipped — every `RunCellAsync` call (manual, via `RunAll`, or
  scheduled) appends a row to the new `notebook_cell_run` table instead of only overwriting the cell's own
  `LastRunStatus`. History popover on each cell card (mirrors the Comments popover). Not FK'd to the cell
  row, by design — history survives cell deletion.

## Collaboration & sharing

- ~~Per-user sharing/ACLs.~~ Shipped — `NotebookUser` (Editor/Viewer, `notebook_user` DACPAC table)
  additive to `IsShared`; `ShareNotebookModal.razor` + `api/query-notebooks/{id}/sharing`. Enforcement
  tightened as a side effect: cell add/edit/run/reorder now actually check owner/admin/`IsShared`/Editor-grant
  (previously any company `EDIT_DATA` user could mutate any notebook's cells regardless of privacy).
- ~~Comments/threads on a cell.~~ Shipped — `NotebookCellComment` (`notebook_cell_comment` DACPAC table,
  mirrors `DataTableComment`), `NotebookCommentService`, a Comments popover on each cell card with the same
  `@mention` autocomplete as `CommentsSidebar`. Mention emails reuse the existing Resend
  `/api/email/comment-mention` route via a new `SendNotebookMentionNotificationAsync` — no new email
  template was added, so the email copy still reads "dataset/table" wording, just populated with the
  notebook/cell name.
- ~~Duplicate/"Save as" a notebook.~~ Shipped — `POST /api/query-notebooks/{id}/duplicate`. Cloned cells
  are renamed (`_copy`, `_copy2`, …) when their `(dataset, name)` would collide with the still-existing
  original, and any cross-cell SQL referencing the old name is best-effort rewritten (whole-word regex,
  not a real SQL parse).
- ~~Export/import a notebook as JSON.~~ Shipped — `GET .../export` / `POST .../import`. A cell's
  `DatasetId` only survives the round trip if the importing company can actually access that dataset;
  otherwise the cell imports with no dataset (same as a freshly-added one).

## Notebook UX

- ~~Cell templates/snippets library for common query patterns.~~ Shipped — "▾ Templates" popover in the
  add-cell row.
- ~~Drag-to-reorder cells in the UI.~~ Shipped — grip handle + `PUT .../cells/reorder`.
- ~~Inline column autocomplete in the SQL editor, sourced from the cell's dataset schema.~~ Shipped —
  `GET /api/datasets/{id}/tables/columns` (bulk) feeds `MonacoSqlEditor`'s `Columns` param; qualified
  (`table.`) and unqualified column suggestions. Live External-source (non-snapshot) tables still only get
  table-name completion — no column endpoint exists for a live source yet.
- ~~Dependency graph view visualizing which cells feed which.~~ Shipped —
  `NotebookDependencyGraph.razor`, opened via the "Dependency Graph" button next to Run All. Pure
  client-side SVG layout from `ReferencedCellIds`; clicking a node scrolls to that cell.

## Output & visualization

- ~~Chart cells — render a materialized cell's result as a bar/line/pie chart.~~ Shipped — the expanded
  result modal's Chart tab (`ResultChart.razor`).
- ~~One-click export of a cell's result to CSV/Excel.~~ Shipped — CSV/Excel buttons on the result toolbar
  and in the expanded modal. Excel export has no writer library in the solution, so it serves an HTML
  table with a `.xls` extension/MIME type (Excel opens it natively) rather than a real `.xlsx` — swap in
  ClosedXML server-side if a genuine `.xlsx` becomes a requirement.

## Ops & governance

- ~~Failure alerts (email) when a scheduled run fails.~~ Shipped, scheduled-run-only (interactive `RunAll`
  failures already show a toast, and emailing a static ops list on every failed iteration while someone's
  actively debugging their own SQL would be noisy). `IIncidentNotificationService.NotifyGenericAsync`
  reuses the existing incident email transport (already registered in both processes) with a new
  `NotebookOpsOptions.FailureRecipients` static list — empty by default, so **no email goes out until
  `NotebookOps:FailureRecipients` is configured** in `Application.Scheduler/appsettings.json`.
- ~~Audit-log notebook runs to ClickHouse `data_app_log`.~~ Shipped — added `"QueryNotebooks"` to
  `DataActivityLogFilter.TargetControllers` plus friendly action-name labels. Notebook/cell ids land in the
  log's `details` JSON (no dedicated column for them), same as other route/action args.
- ~~Storage/row-count indicator per notebook.~~ Shipped — `GetStorageSummaryAsync` groups the notebook's
  materialized cells by dataset, pulls that dataset's per-table stats once, and keeps only the rows
  matching this notebook's own object names (a dataset's `.duckdb` file can hold unrelated tables too, so
  this can't just reuse the dataset-wide summary). Shown as a small stat line under the notebook title.
