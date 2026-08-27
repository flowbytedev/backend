using Application.Authorization;
using Application.Client.Pages;
using Application.DailyInventory;
using Hangfire;
using Hangfire.SqlServer;
using Application.Components;
using Application.Components.Account;
using Application.Helpers;
using Application.Hubs;
using Application.Services;
using Application.Services.Data;
using Application.Shared.Authorization;
using Application.Shared.Data;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Models.User;
using Application.Shared.Services;
using Application.Shared.Services.Data;
using Application.Shared.Services.Org;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;



var builder = WebApplication.CreateBuilder(args);

AppContext.SetSwitch("DuckDB.NET.Native.DisableLibraryLoad", false);


builder.Services.AddControllers(options =>
{
    // Audit/usage logging for Datasets & Tables API calls (no-ops for other controllers / when disabled).
    options.Filters.Add<Application.Logging.DataActivityLogFilter>();
});


// Add services to the container.
builder.Services.AddRazorComponents()
    .AddAuthenticationStateSerialization()
    .AddInteractiveWebAssemblyComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .AllowAnyOrigin()     // or .WithOrigins("https://your-frontend.com")
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});


builder.Services.AddFluentUIComponents();

builder.Services.AddCascadingAuthenticationState();
//builder.Services.AddAuthorizationCore();

builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, PersistingServerAuthenticationStateProvider>();

builder.Services.AddApiAuthorization();

const string MS_OIDC_SCHEME = "MicrosoftOidc";

// Per-company, role-based authorization policies (shared with the WASM client).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentCompanyAccessor, HttpContextCompanyAccessor>();
builder.Services.AddScoped<IAuthorizationHandler, ModuleAccessHandler>();
builder.Services.AddAuthorization(options => options.AddFlowbytePolicies());
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;


    }).AddCookie("Identity.Application")
    .AddCookie("Identity.External")
    //.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)

    // API-key scheme for external, non-interactive data access (used only by ExternalDataController).
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Application.Authorization.ApiKeyAuthenticationHandler>(
        Application.Authorization.ApiKeyAuthenticationDefaults.Scheme, _ => { })

    .AddOpenIdConnect(MS_OIDC_SCHEME, displayName: "Continue with Microsoft" , options =>
    {

        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.ClientId = builder.Configuration["AzureAd:ClientId"];
        options.ClientSecret = builder.Configuration["AzureAd:ClientSecret"];
        options.Authority = builder.Configuration["AzureAd:Authority"];
        options.MetadataAddress = builder.Configuration["AzureAd:MetadataAddress"];
        options.CallbackPath = builder.Configuration["AzureAd:CallbackPath"];
        options.RequireHttpsMetadata = false;

        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;

        options.SignedOutRedirectUri = builder.Configuration["AzureAd:SignedOutRedirectUri"];
        options.SignedOutCallbackPath = builder.Configuration["AzureAd:SignedOutCallbackPath"];
        options.ResponseType = OpenIdConnectResponseType.Code;


        // .NET 9 feature
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.TokenValidationParameters.NameClaimType = JwtRegisteredClaimNames.Name;
        options.TokenValidationParameters.RoleClaimType = "role";

        // CRITICAL: Use Object ID (oid) as NameIdentifier instead of sub
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = context =>
            {
                var identity = context.Principal.Identity as ClaimsIdentity;
                
                if (identity != null)
                {
                    // Get the Object ID claim
                    var oidClaim = context.Principal.FindFirst(
                        "http://schemas.microsoft.com/identity/claims/objectidentifier");
                    
                    if (oidClaim != null)
                    {
                        // Remove existing NameIdentifier (sub claim)
                        var existingNameId = identity.FindFirst(ClaimTypes.NameIdentifier);
                        if (existingNameId != null)
                        {
                            identity.RemoveClaim(existingNameId);
                        }
                        
                        // Add Object ID as NameIdentifier
                        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, oidClaim.Value));
                    }
                }
                
                return Task.CompletedTask;
            }
        };

    });



var connectionString = builder.Configuration.GetConnectionString("ApplicationDbContext") ?? throw new InvalidOperationException("Connection string 'ApplicationDbContext' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString, b => b.MigrationsAssembly("Application")));

var statusConnectionString = builder.Configuration.GetConnectionString("StatusDbContext") ?? connectionString;
builder.Services.AddDbContext<StatusDbContext>(options =>
    options.UseSqlServer(statusConnectionString, b => b.MigrationsAssembly("Application")));


var userManagementConnectionString = builder.Configuration.GetConnectionString("UserManagementDbContext") ?? throw new InvalidOperationException("Connection string 'ApplicationDbContext' not found.");
// Register a factory so services (e.g. CompanyService) can create short-lived, non-shared
// contexts. This prevents "A second operation was started on this context instance" when a
// layout and a page issue concurrent queries during Blazor SSR rendering. Identity still needs
// a scoped UserManagementDbContext, so also expose one that is created from the factory.
builder.Services.AddDbContextFactory<UserManagementDbContext>(options =>
    options.UseSqlServer(userManagementConnectionString, b => b.MigrationsAssembly("Application")));
builder.Services.AddScoped<UserManagementDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<UserManagementDbContext>>().CreateDbContext());


// Add Data Warehouse DbContext
var dataWarehouseConnectionString = builder.Configuration.GetConnectionString("DataWarehouseDbContext");
if (!string.IsNullOrEmpty(dataWarehouseConnectionString))
{
    builder.Services.AddDbContext<DataWarehouseDbContext>(options =>
        options.UseSqlServer(dataWarehouseConnectionString));
}

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<UserManagementDbContext>()
    .AddSignInManager()
    .AddRoles<IdentityRole>()
    .AddRoleManager<RoleManager<IdentityRole>>()
    .AddRoleStore<RoleStore<IdentityRole, UserManagementDbContext>>()
    .AddUserStore<UserStore<ApplicationUser, IdentityRole, UserManagementDbContext>>()
    .AddDefaultTokenProviders();


// Add services to the container.
builder.Services.AddMemoryCache();

// ETL pipeline limits, and the blob settings its file source finally reads.
var pipelineOptions = new Application.Shared.Models.Data.Pipelines.PipelineOptions();
builder.Configuration.Bind("Pipelines", pipelineOptions);
builder.Services.AddSingleton(pipelineOptions);

var azureBlobOption = new Application.Shared.Models.Data.Pipelines.AzureBlobOption();
builder.Configuration.Bind("AzureBlob", azureBlobOption);
builder.Services.AddSingleton(azureBlobOption);

// Bind DuckdbOptions
var duckdbOption = new DuckdbOption();
builder.Configuration.Bind("Duckdb", duckdbOption);
// Register with DI
builder.Services.AddSingleton(duckdbOption);


builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

builder.Services.AddScoped<StateContainer>();
builder.Services.AddScoped<ClientAuthenticationDetail>();

builder.Services.AddScoped<ICompanyService, CompanyService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IApplicationAccessService, ApplicationAccessService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IDatasetService, DatasetService>();
builder.Services.AddScoped<IDuckdbService, DuckdbService>();

// ---- ETL pipelines -------------------------------------------------------------------------------
// IPipelineStore is DuckdbService's second interface, not a second service: it needs that class's
// private path resolution and PromoteRelationAsync. Resolved from the same scoped instance so a run
// shares one dataset-path cache.
builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IPipelineStore>(
    sp => (DuckdbService)sp.GetRequiredService<IDuckdbService>());
builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IPipelineFileResolver,
    Application.Shared.Services.Data.Pipelines.PipelineFileResolver>();
builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IPipelineSourceLoader,
    Application.Shared.Services.Data.Pipelines.PipelineSourceLoader>();
builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IPipelineEngine,
    Application.Shared.Services.Data.Pipelines.PipelineEngine>();
builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IExternalTableWriter,
    Application.Shared.Services.Data.Pipelines.ExternalTableWriter>();

// The API source and destination. One named HttpClient for both, with automatic redirects DISABLED on
// purpose: .NET drops the Authorization header when it follows a redirect to another origin, but it keeps
// custom headers, so an X-Api-Key credential would be handed to whatever host a remote server named in a
// Location response. PipelineApiClient follows same-origin redirects itself and refuses the rest.
builder.Services.AddHttpClient(Application.Shared.Services.Data.Pipelines.PipelineApiClient.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });

builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IPipelineApiClient, Application.Shared.Services.Data.Pipelines.PipelineApiClient>();
builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IPipelineApiReader, Application.Shared.Services.Data.Pipelines.PipelineApiReader>();
builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IPipelineApiWriter, Application.Shared.Services.Data.Pipelines.PipelineApiWriter>();

// ---- Pipeline export email (destination.email) ----------------------------------------------------
// Same Next.js/Resend service as every other email here, but registered for the pipeline engine rather
// than for a notification service: this one's failures are returned, because a scheduled export that
// silently does not arrive is a report somebody is waiting on.
builder.Services.Configure<Application.Shared.Options.PipelineEmailOptions>(
    builder.Configuration.GetSection("PipelineEmail"));
builder.Services.AddHttpClient(Application.Shared.Services.Data.Pipelines.PipelineEmailSender.HttpClientName,
    (sp, client) =>
    {
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Application.Shared.Options.PipelineEmailOptions>>().Value;
        if (string.IsNullOrWhiteSpace(opts.ApiBaseUri)) return;
        client.BaseAddress = new Uri(opts.ApiBaseUri);
        // Generous: the request body carries a base64 attachment, so it is an upload, not a ping.
        client.Timeout = TimeSpan.FromSeconds(opts.ResolveTimeoutSeconds());
    });
builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IPipelineExportWriter,
    Application.Shared.Services.Data.Pipelines.PipelineExportWriter>();
builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IPipelineEmailSender,
    Application.Shared.Services.Data.Pipelines.PipelineEmailSender>();


builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.IPipelineService,
    Application.Shared.Services.Data.Pipelines.PipelineService>();
// The web app only ever ENQUEUES this; the scheduler executes it. Registered here so Hangfire can
// serialize the call, and because "Run now" from the editor goes through the same wrapper as a cron fire.
builder.Services.AddScoped<Application.Shared.Services.Data.Pipelines.PipelineJob>();
builder.Services.AddScoped<ICommentService, CommentService>();
builder.Services.AddScoped<IUserPreferencesService, UserPreferencesService>();
builder.Services.AddScoped<IUserSearchService, UserSearchService>();
builder.Services.AddScoped<Application.Shared.Services.IWhatsNewService, Application.Shared.Services.WhatsNewService>();

// Conversational dashboard builder (AI): dashboard/widget CRUD + the planner agent.
builder.Services.AddScoped<Application.Shared.Services.Data.IAiDashboardService, Application.Shared.Services.Data.AiDashboardService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IDashboardAgentService, Application.Shared.Services.Data.DashboardAgentService>();

// Semantic layer (AI dataset documentation): per-column doc CRUD + the AI generator.
builder.Services.AddScoped<Application.Shared.Services.Data.IDatasetDocService, Application.Shared.Services.Data.DatasetDocService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IColumnDocGenerationService, Application.Shared.Services.Data.ColumnDocGenerationService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IPublicDatasetApiService, Application.Shared.Services.Data.PublicDatasetApiService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IUserDatasetPreferenceService, Application.Shared.Services.Data.UserDatasetPreferenceService>();

// Query notebooks (MotherDuck-style): notebook/cell CRUD + execution engine + the AI-assist planner.
builder.Services.AddScoped<Application.Shared.Services.Data.IQueryNotebookService, Application.Shared.Services.Data.QueryNotebookService>();
builder.Services.AddScoped<Application.Shared.Services.Data.INotebookAgentService, Application.Shared.Services.Data.NotebookAgentService>();
builder.Services.AddScoped<Application.Shared.Services.Data.INotebookSharingService, Application.Shared.Services.Data.NotebookSharingService>();
builder.Services.AddScoped<Application.Shared.Services.Data.INotebookCommentService, Application.Shared.Services.Data.NotebookCommentService>();
builder.Services.AddSingleton<Application.Shared.Services.Data.INotebookRunCancellationRegistry, Application.Shared.Services.Data.NotebookRunCancellationRegistry>();
builder.Services.Configure<Application.Shared.Options.NotebookOpsOptions>(builder.Configuration.GetSection("NotebookOps"));

// Metrics Services
builder.Services.AddScoped<IMetricService, MetricService>();
builder.Services.AddScoped<IMetricTargetService, MetricTargetService>();
builder.Services.AddScoped<IMetricValueService, MetricValueService>();
builder.Services.AddScoped<IClickHouseService, ClickHouseService>();

// Daily Inventory (ClickHouse reporting)
builder.Services.AddDailyInventory(builder.Configuration);

// Data app audit/usage log (ClickHouse data_app_log). Singleton buffer + one background writer.
var dataAppLogSettings = builder.Configuration.GetSection("DataAppLog").Get<Application.Shared.Services.Logging.DataAppLogSettings>()
    ?? new Application.Shared.Services.Logging.DataAppLogSettings();
builder.Services.AddSingleton(dataAppLogSettings);
builder.Services.AddSingleton<Application.Shared.Services.Logging.IDataAppLogService, Application.Shared.Services.Logging.DataAppLogService>();
builder.Services.AddHostedService<Application.Logging.DataAppLogHostedService>();

// Per-company settings (the debug-logging toggle) + the gated debug-log emit helper (writes into data_app_log).
builder.Services.AddScoped<Application.Shared.Services.ICompanySettingsService, Application.Shared.Services.CompanySettingsService>();
builder.Services.AddScoped<Application.Shared.Services.Logging.IDebugLogService, Application.Shared.Services.Logging.DebugLogService>();

// Dashboards (OOS dashboard + dashboard/table links) — Application.Dashboard feature project.
Application.Dashboard.DashboardServiceExtensions.AddDashboard(builder.Services, builder.Configuration);

// Add Data Warehouse Service
builder.Services.AddScoped<DataWarehouseService>();

// Add KPI Dashboard Service (reads warehouse org.kpi)
builder.Services.AddScoped<IKpiDashboardService, KpiDashboardService>();

// Add Dataset Sharing Services
builder.Services.AddScoped<IDatasetSharingService, DatasetSharingService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IEmailNotificationService, Application.Services.Data.EmailNotificationService>();

// Add Real-Time Data Service
builder.Services.AddScoped<IRealTimeDataService, RealTimeDataService>();
builder.Services.AddScoped<ISalesDataSignalRService, SalesDataSignalRService>();
builder.Services.AddScoped<ISalesDashboardService, SalesDashboardService>();

// Add Email Helper
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));
builder.Services.AddScoped<EmailHelper>();

// Status Module Services
builder.Services.AddScoped<IIncidentService, IncidentService>();
builder.Services.AddScoped<IMonitoredAssetService, MonitoredAssetService>();
builder.Services.AddScoped<IAssetStatusHistoryService, AssetStatusHistoryService>();
builder.Services.AddScoped<IStatusOverviewService, StatusOverviewService>();
builder.Services.AddScoped<IEntityAudienceService, EntityAudienceService>();

// Incident notification emails (Resend microservice)
builder.Services.Configure<Application.Shared.Options.IncidentEmailOptions>(
    builder.Configuration.GetSection("IncidentNotificationEmail"));
builder.Services.AddScoped<IIncidentNotificationService, IncidentNotificationService>();
builder.Services.AddHttpClient(IncidentNotificationService.HttpClientName, (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Application.Shared.Options.IncidentEmailOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.ApiBaseUri)) return;
    client.BaseAddress = new Uri(opts.ApiBaseUri);
    client.Timeout = TimeSpan.FromSeconds(60);
});

// Dataset-shared notification emails (same Resend microservice).
builder.Services.Configure<Application.Shared.Options.DatasetSharedEmailOptions>(
    builder.Configuration.GetSection("DatasetSharedEmail"));
builder.Services.AddHttpClient(Application.Services.Data.EmailNotificationService.HttpClientName, (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Application.Shared.Options.DatasetSharedEmailOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.ApiBaseUri)) return;
    client.BaseAddress = new Uri(opts.ApiBaseUri);
    client.Timeout = TimeSpan.FromSeconds(60);
});

// Comment-mention notification emails (same Resend microservice).
builder.Services.Configure<Application.Shared.Options.CommentMentionEmailOptions>(
    builder.Configuration.GetSection("CommentMentionEmail"));
builder.Services.AddScoped<Application.Shared.Services.Data.ICommentMentionNotificationService, Application.Services.Data.CommentMentionNotificationService>();
builder.Services.AddHttpClient(Application.Services.Data.CommentMentionNotificationService.HttpClientName, (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Application.Shared.Options.CommentMentionEmailOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.ApiBaseUri)) return;
    client.BaseAddress = new Uri(opts.ApiBaseUri);
    client.Timeout = TimeSpan.FromSeconds(60);
});

// Table-moved notification emails (same Resend microservice).
builder.Services.Configure<Application.Shared.Options.TableMovedEmailOptions>(
    builder.Configuration.GetSection("TableMovedEmail"));
builder.Services.AddScoped<Application.Shared.Services.Data.ITableMovedNotificationService, Application.Services.Data.TableMovedNotificationService>();
builder.Services.AddHttpClient(Application.Services.Data.TableMovedNotificationService.HttpClientName, (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Application.Shared.Options.TableMovedEmailOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.ApiBaseUri)) return;
    client.BaseAddress = new Uri(opts.ApiBaseUri);
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddScoped<Application.Shared.Services.Data.IDatasetTableMoveService, Application.Shared.Services.Data.DatasetTableMoveService>();

// Server Management (credentials + remote service start/stop)
// Keys must persist OUTSIDE the app folder so redeploys don't wipe them — losing the key ring
// makes every stored credential undecryptable. Configurable via DataProtection:KeysPath
// (e.g. "C:\\ProgramData\\FlowByte\\keys" on the Azure VM); falls back to App_Data for local dev.
var keysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(keysPath);

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("FlowByte.Application")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Encrypt the key ring at rest with the machine's DPAPI key (Windows only).
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddSingleton<ICredentialProtector, CredentialProtector>();
builder.Services.AddScoped<IServerCredentialService, ServerCredentialService>();
builder.Services.AddScoped<IServerManagementService, ServerManagementService>();
builder.Services.AddScoped<IRemoteServerExecutor, SshServerExecutor>();

// Power BI dataset refresh (service-principal connections + refresh history/trigger).
// Reuses ICredentialProtector (registered above) to encrypt connection secrets at rest.
builder.Services.AddScoped<IPowerBiConnectionService, PowerBiConnectionService>();
builder.Services.AddScoped<IPowerBiService, PowerBiService>();
builder.Services.AddHttpClient(PowerBiService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

// Database table discovery for Database-type entities (per-entity encrypted connection,
// lists tables across MSSQL/PostgreSQL/MySQL/ClickHouse/DuckDB, materializes Table entities).
builder.Services.AddScoped<IDatabaseTableService, DatabaseTableService>();

// Database administration for the same entities (space usage, user provisioning). Uses a separate
// elevated credential — the connection above is deliberately least-privilege and read-only.
builder.Services.AddScoped<IDatabaseAdminService, DatabaseAdminService>();

// AI-assisted schema (column data type) inference for data import
builder.Services.AddScoped<ISchemaInferenceService, SchemaInferenceService>();

// External-access API keys (issue/scope/validate) + the data API they unlock.
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();

// SQL query workbench — saved queries (ad-hoc execution lives on IDuckdbService).
builder.Services.AddScoped<ISavedQueryService, SavedQueryService>();

// Shared "what tables does this SQL touch, and may the user read them" resolution. Used by the
// workbench (permissive policy) and the public query endpoint (strict policy).
builder.Services.AddScoped<Application.Shared.Services.Data.ISqlTableResolver, Application.Shared.Services.Data.SqlTableResolver>();

// Public API SQL execution (api/dataset/{id}/query/run). This service is the enforcement point for
// column masking and row-level security — the consuming app cannot apply them.
var publicApiOptions = new PublicApiOptions();
builder.Configuration.Bind("PublicApi", publicApiOptions);
builder.Services.AddSingleton(publicApiOptions);
builder.Services.AddScoped<Application.Shared.Services.Data.IPublicSqlQueryService, Application.Shared.Services.Data.PublicSqlQueryService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IPublicApiUserAuthorizationService, Application.Shared.Services.Data.PublicApiUserAuthorizationService>();

// Rate limiting, scoped to the public SQL endpoint only (nothing else in the app is affected).
// This endpoint runs arbitrary model-generated SQL, so it needs limits the rest of the app does not.
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiter.AddPolicy(Application.Controllers.PublicQueryController.RateLimitPolicy, context =>
    {
        var apiKeyId = context.User.FindFirst("api_key_id")?.Value
                       ?? context.Connection.RemoteIpAddress?.ToString()
                       ?? "anonymous";
        var actingUser = context.Request.Headers["X-User-Id"].FirstOrDefault()
                         ?? context.Request.Headers["Userid"].FirstOrDefault()
                         ?? "-";

        // Partitioned per (key, acting user) so one runaway user cannot starve the whole integration.
        // A caller varying X-User-Id multiplies its own budget, which is why the concurrency limiter
        // below is partitioned on the key alone.
        return RateLimitPartition.GetSlidingWindowLimiter($"{apiKeyId}|{actingUser}", _ =>
            new SlidingWindowRateLimiterOptions
            {
                PermitLimit = publicApiOptions.EffectiveRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
    });

    // Concurrency matters more than rate here: each DuckDB read opens a file handle against a local
    // file, so a handful of simultaneous full scans exhausts I/O long before a per-minute counter trips.
    // It also has to be partitioned on the key ALONE — the sliding window above is per (key, user), so a
    // caller varying X-User-Id would otherwise multiply its own budget.
    //
    // A second AddPolicy would not work: [EnableRateLimiting] binds exactly one policy per endpoint, and
    // the attribute above already spends it. The global limiter runs in ADDITION to the endpoint policy,
    // so the concurrency limit goes there — returning a no-op partition for every request that is not
    // headed for this endpoint, which leaves the rest of the app untouched.
    rateLimiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var policy = context.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        if (policy != Application.Controllers.PublicQueryController.RateLimitPolicy)
            return RateLimitPartition.GetNoLimiter("unlimited");

        var apiKeyId = context.User.FindFirst("api_key_id")?.Value
                       ?? context.Connection.RemoteIpAddress?.ToString()
                       ?? "anonymous";
        return RateLimitPartition.GetConcurrencyLimiter($"concurrency|{apiKeyId}", _ =>
            new ConcurrencyLimiterOptions
            {
                PermitLimit = publicApiOptions.EffectiveMaxConcurrent,
                QueueLimit = publicApiOptions.EffectiveConcurrencyQueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    });
});

// Scheduled/automated ingestion — executor shared with the scheduler ("Run now" runs it inline here).
builder.Services.AddScoped<IIngestionService, IngestionService>();

// AI-assisted ingestion: conversational planner that drafts an ingestion source config (never persists).
builder.Services.AddScoped<Application.Shared.Services.Data.IIngestionAgentService, Application.Shared.Services.Data.IngestionAgentService>();

// Hangfire CLIENT only (no server): lets "Run as batch" enqueue an ingestion job into the same storage
// the Application.Scheduler process reads from, so it runs there instead of inline in this web request.
// Requires the 'SchedulerDbContext' connection string; without it, batch mode stays disabled and "Run
// now" (inline) still works. Serializer settings mirror the scheduler so jobs deserialize cleanly.
var hangfireConnectionString = builder.Configuration.GetConnectionString("SchedulerDbContext");
if (!string.IsNullOrWhiteSpace(hangfireConnectionString))
{
    builder.Services.AddHangfire(cfg => cfg
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(hangfireConnectionString, new SqlServerStorageOptions
        {
            SchemaName = "HangFire",
            PrepareSchemaIfNecessary = true,
        }));
    // No AddHangfireServer() here — the scheduler process is the only worker.
}

// Configure Azure OpenAI settings
builder.Services.Configure<AzureOpenAIConfiguration>(builder.Configuration.GetSection("AzureOpenAI"));

// Add EmailSettings configuration
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));

// Add HTTP client factory
builder.Services.AddHttpClient();

// Register EmailHelper as a singleton
builder.Services.AddSingleton<EmailHelper>();


builder.Services.Configure<IdentityOptions>(options =>
{
    options.ClaimsIdentity.UserIdClaimType = ClaimTypes.NameIdentifier;
    options.ClaimsIdentity.UserNameClaimType = ClaimTypes.Name;
    options.ClaimsIdentity.RoleClaimType = ClaimTypes.Role;
    //options.ClaimsIdentity.EmailClaimType = ClaimTypes.Email;
    //options.User.RequireUniqueEmail = true;

});

// get the uri from the appsettings.json
var uri = builder.Configuration["BaseUri"];
//// Configure the HttpClient to include the user's access token when calling the API
builder.Services.AddHttpClient("Application.ServerAPI", client => client.BaseAddress = new Uri(uri));

builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("Application.ServerAPI"));


builder.Services.AddSignalR();

builder.Services.AddResponseCompression(opts =>
{
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/octet-stream"]);
});


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.Use((context, next) =>
{
    context.Request.Scheme = "https";
    return next();
});

app.UseHttpsRedirection();

// Must sit before MapControllers so the [EnableRateLimiting] metadata on PublicQueryController is seen.
app.UseRateLimiter();

app.MapControllers();

app.UseStaticFiles();
app.UseAntiforgery();

app.UseCors("AllowAll");

// Refuse anyone who does not hold access to this application in the shared identity database.
// Placed here so static files and CORS preflight are already handled, and before endpoint
// execution. See Middleware/ApplicationAccessMiddleware.cs for why this is not an auth policy.
app.UseMiddleware<Application.Middleware.ApplicationAccessMiddleware>();


app.MapHub<NotificationHub<DataJob>>("/notification/datajob");
app.MapHub<SalesDataHub>("/realtime/salesdata");
app.UseResponseCompression();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Application.Client._Imports).Assembly);

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

// A "Run now" ingestion executes inline in the web request, so a restart mid-run can leave its run
// record stuck at "Running". Reconcile any such orphaned runs once at startup.
using (var startupScope = app.Services.CreateScope())
{
    try
    {
        var ingestion = startupScope.ServiceProvider.GetRequiredService<IIngestionService>();
        var staleHours = builder.Configuration.GetValue<double?>("Ingestion:StaleRunTimeoutHours") ?? 6;
        await ingestion.FailStaleRunsAsync(TimeSpan.FromHours(staleHours));
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Stale ingestion run reconciliation at startup failed.");
    }
}

app.Run();
