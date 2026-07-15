# Copilot Instructions

## Build & Run

```bash
# Run the main web app
cd Application && dotnet run

# Apply EF Core migrations
cd Application && dotnet ef database update

# Add a new migration
cd Application && dotnet ef migrations add <MigrationName>

# Run the scheduler (separate process)
cd Application.Scheduler && dotnet run

# Run the email preview server (React Email / Next.js)
cd Application.Email && npm install && npm run dev
```

There are no automated tests in this repository.

## Architecture

This is a **multi-project .NET 9 + Blazor solution**:

| Project | Role |
|---|---|
| `Application` | ASP.NET Core host: serves Blazor SSR/WASM, REST API controllers, SignalR hubs, auth |
| `Application.Client` | Blazor WebAssembly client — pages and components rendered in-browser |
| `Application.Shared` | Shared class library — models, EF DbContexts, service interfaces + implementations |
| `Application.Scheduler` | Standalone Hangfire background job service (separate process) |
| `Application.Email` | React Email (Next.js) project for building email templates |

**Rendering modes**: Blazor pages use both `InteractiveWebAssemblyRenderMode` and `InteractiveServerRenderMode`. `Application.Client` assemblies are added as additional assemblies to the server app.

**Data stores**: SQL Server (primary via EF Core), ClickHouse (`IClickHouseService`), DuckDB (`IDuckdbService`).

**Real-time**: SignalR hubs at `/notification/datajob` (`NotificationHub<DataJob>`) and `/realtime/salesdata` (`SalesDataHub`).

**AI**: Azure OpenAI (bound to `AzureOpenAIConfiguration`) configured via `AzureOpenAI` appsettings section; used directly by feature-specific agent services (`DashboardAgentService`, `NotebookAgentService`, `IngestionAgentService`, etc.).

## Key Conventions

### Multi-tenancy via `CompanyId`
Every entity model inherits `BaseModel`, which includes `CompanyId` (foreign key to `Company`). **All data queries must filter by `companyId`**. API controllers receive tenant context exclusively via the `X-Company-Id` request header:

```csharp
[HttpGet]
public async Task<ActionResult<IEnumerable<Metric>>> GetMetrics(
    [FromHeader(Name = "X-Company-Id")] string companyId)
```

### Snake_case DB columns
`ApplicationDbContext.OnModelCreating` automatically converts all C# PascalCase property names to `snake_case` column names in SQL Server unless a `[Column]` attribute is explicitly applied. Do **not** add manual `[Column("...")]` attributes for standard name conversions.

### No cascade deletes
All foreign key relationships are globally configured to `DeleteBehavior.Restrict`. Handle orphan cleanup explicitly in service methods.

### Service layer pattern
- Interfaces live in `Application.Shared/Services/`
- Implementations also live in `Application.Shared/Services/` (or subdirectories)
- Controllers in `Application/Controllers/` are thin: they validate input/headers and delegate everything to the injected service interface

### Authentication
Microsoft Entra ID (Azure AD) OIDC via `AddOpenIdConnect`. The `OnTokenValidated` event replaces the default `sub` claim with the Azure AD Object ID (`oid`) as `ClaimTypes.NameIdentifier`. Use `User.FindFirst(ClaimTypes.NameIdentifier)?.Value` in controllers to get the user ID.

### Migrations
EF Core migrations are stored in `Application/Migrations/` with `MigrationsAssembly("Application")`. Both `ApplicationDbContext` and `UserManagementDbContext` use this assembly. The `SchedulerDbContext` uses `Application.Scheduler` as its migrations assembly.

### Metric Filters
Dynamic SQL filters on metrics use `;` as a multi-value separator, which generates `IN(...)` SQL clauses. Filter types: `text`, `date`, `number`, `select`. See `METRIC_FILTERS_GUIDE.md` for full details.

### UI Components
Uses **Microsoft Fluent UI Blazor** (`Microsoft.FluentUI.AspNetCore.Components`). Custom reusable components (dialogs, tables, toolbars) are in `Application.Client/Components/`.

### Configuration
- `BaseUri` in `appsettings.json` sets the HTTP client base address for server-side calls
- `Duckdb:DuckdbFilePath` sets the DuckDB file location
- `AzureAd` section holds all OIDC configuration
