# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Run the main web app (Blazor host + API)
cd Application && dotnet run

# Run the scheduler (separate Hangfire process)
cd Application.Scheduler && dotnet run

# Build the whole solution
dotnet build Application.sln

# Applicat.Database.{Database name}
Create all the tables in the application.database project DACPAC

# Email template preview server (React Email / Next.js)
cd Application.Email && npm install && npm run dev
```

There are **no automated tests** in this repository.

## Architecture

Multi-project **.NET 9 + Blazor** solution (`Application.sln`):

| Project | Role |
|---|---|
| `Application` | ASP.NET Core host: serves Blazor (SSR + WASM), REST API controllers, SignalR hubs, Identity/auth. Owns EF migrations. |
| `Application.Client` | Blazor WebAssembly client — pages/components rendered in-browser. Added as additional assembly to the server app. |
| `Application.Shared` | Class library shared by all .NET projects — models, EF `DbContext`s, service interfaces + implementations. |
| `Application.Scheduler` | Standalone Hangfire console/web app (separate process) running recurring data-sync and email jobs. |
| `Application.Email` | React Email (Next.js, TypeScript) project for authoring email templates — not part of the .NET solution. |

**Rendering**: Pages use both `InteractiveWebAssemblyRenderMode` and `InteractiveServerRenderMode`; the host adds `Application.Client._Imports` assembly via `AddAdditionalAssemblies`.

**Data stores**: SQL Server is primary (multiple `DbContext`s — see below). Also DuckDB (`IDuckdbService`) and ClickHouse (`IClickHouseService`) for analytical queries.

**Real-time**: SignalR hubs at `/notification/datajob` (`NotificationHub<DataJob>`) and `/realtime/salesdata` (`SalesDataHub`).

**AI**: Azure OpenAI, configured from the `AzureOpenAI` appsettings section (bound to `AzureOpenAIConfiguration`). It is used directly by feature-specific agent services — `DashboardAgentService`, `NotebookAgentService`, `IngestionAgentService`, `ColumnDocGenerationService`, `SchemaInferenceService`.

### DbContexts (all SQL Server, separate connection strings)
- `ApplicationDbContext` — primary app data. Migrations in `Application/Migrations/`.
- `UserManagementDbContext` — ASP.NET Identity store (users/roles). Migrations in `Application`.
- `StatusDbContext` — status/incident monitoring module. Falls back to the app connection string if `StatusDbContext` is not set.
- `DataWarehouseDbContext` — read-only warehouse; only registered if `DataWarehouseDbContext` connection string is present.
- `SchedulerDbContext` — Hangfire job metadata; lives in `Application.Scheduler` and owns its own migrations assembly.

## Key Conventions

### Multi-tenancy via `CompanyId`
Every entity inherits `BaseModel` (`Application.Shared/Models/BaseModel.cs`), which carries `CompanyId`. **All data queries must filter by company.** API controllers receive tenant context exclusively from the `X-Company-Id` request header:
```csharp
public async Task<ActionResult<IEnumerable<Metric>>> GetMetrics(
    [FromHeader(Name = "X-Company-Id")] string companyId)
```

### snake_case columns (automatic)
`ApplicationDbContext.OnModelCreating` (and the other contexts) auto-convert PascalCase table/property names to `snake_case` unless an explicit `[Column]`/`[Table]` attribute is present. **Do not add manual `[Column("...")]` attributes for ordinary name conversions** — it defeats the convention.

### No cascade deletes
All FK relationships are globally set to `DeleteBehavior.Restrict`. Orphan cleanup must be handled explicitly in service methods.

### Service layer
Interfaces and implementations both live in `Application.Shared/Services/` (some under subfolders `Data/`, `Org/`). Controllers in `Application/Controllers/` are thin — validate input/headers, delegate to the injected service. Register new services in `Application/Program.cs`.

### Authentication
Microsoft Entra ID (Azure AD) OIDC via `AddOpenIdConnect`. The `OnTokenValidated` event **replaces the default `sub` claim with the Azure AD Object ID (`oid`)** as `ClaimTypes.NameIdentifier`. Read the user id with `User.FindFirst(ClaimTypes.NameIdentifier)?.Value`. Config is in the `AzureAd` appsettings section.

### Scheduler jobs
`Application.Scheduler/Program.cs` registers Hangfire recurring jobs at startup, iterating over databases from `IDatabaseRepository` to schedule per-store sales sync (`SalesJob`), plus `SalesSnapshotEmailJob` and `AssetPingJob`. Cron times use the `Asia/Beirut` timezone (with a Windows `Middle East Standard Time` fallback). Dashboard is at `/dashboard`.

### Metric filters
Dynamic SQL filters on metrics use `;` as a multi-value separator, which generates `IN(...)` clauses. Filter types: `text`, `date`, `number`, `select`.

### UI
**Microsoft Fluent UI Blazor** (`Microsoft.FluentUI.AspNetCore.Components`). Shared reusable components (dialogs, tables, toolbars, filters) are in `Application.Client/Components/`.

### Configuration notes
- `BaseUri` — base address for the server-side `Application.ServerAPI` HTTP client.
- `Duckdb:DuckdbFilePath` — DuckDB file location.
- Connection strings, `AzureAd`, `AzureOpenAI`, `EmailSettings`, `AzureBlob` all live in `appsettings.json`.
