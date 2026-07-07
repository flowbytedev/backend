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
using Microsoft.AspNetCore.ResponseCompression;
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
builder.Services.AddScoped<Application.Shared.Services.Data.IDatasetService, DatasetService>();
builder.Services.AddScoped<IDuckdbService, DuckdbService>();
builder.Services.AddScoped<ICommentService, CommentService>();
builder.Services.AddScoped<IUserPreferencesService, UserPreferencesService>();
builder.Services.AddScoped<IUserSearchService, UserSearchService>();

// Conversational dashboard builder (AI): dashboard/widget CRUD + the planner agent.
builder.Services.AddScoped<Application.Shared.Services.Data.IAiDashboardService, Application.Shared.Services.Data.AiDashboardService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IDashboardAgentService, Application.Shared.Services.Data.DashboardAgentService>();

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

// Add Chat Service for AI functionality
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IChatMessageRepository, InMemoryChatMessageRepository>();

// AI-assisted schema (column data type) inference for data import
builder.Services.AddScoped<ISchemaInferenceService, SchemaInferenceService>();

// External-access API keys (issue/scope/validate) + the data API they unlock.
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();

// SQL query workbench — saved queries (ad-hoc execution lives on IDuckdbService).
builder.Services.AddScoped<ISavedQueryService, SavedQueryService>();

// Scheduled/automated ingestion — executor shared with the scheduler ("Run now" runs it inline here).
builder.Services.AddScoped<IIngestionService, IngestionService>();

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

app.MapControllers();

app.UseStaticFiles();
app.UseAntiforgery();

app.UseCors("AllowAll");

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
