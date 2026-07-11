using System.Net.Http.Headers;
using Application.Scheduler.Jobs;
using Application.Scheduler.Options;
using Application.Scheduler.Repositories;
using Application.Scheduler.Services;
using Application.Shared.Data;
using Application.Shared.Models.Data;
using Microsoft.AspNetCore.DataProtection;
using Hangfire;
using Hangfire.Console;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;




var builder = WebApplication.CreateBuilder(args); // Or use your own Host Builder

var navConnectionString = builder.Configuration.GetConnectionString("NavDbContext") ?? throw new InvalidOperationException("Connection string 'NavDbContext' not found.");
var appConnectionString = builder.Configuration.GetConnectionString("ApplicationDbContext") ?? throw new InvalidOperationException("Connection string 'ApplicationDbContext' not found.");
var connectionString = builder.Configuration.GetConnectionString("SchedulerDbContext") ?? throw new InvalidOperationException("Connection string 'SchedulerDbContext' not found.");


// get the configuration from appsettings.json
var configuration = builder.Configuration;

// get d365 url from appsettings.json
var d365Url = configuration[$"NokNok_D365:ApiUri"];


// get the app registration details from app settings
var aadTenant = configuration[$"NokNok_D365:AppRegistration:Tenant"];
var aadResource = configuration[$"NokNok_D365:AppRegistration:Resource"];
var aadClientAppId = configuration[$"NokNok_D365:AppRegistration:ClientId"];
var aadClientAppSecret = configuration[$"NokNok_D365:AppRegistration:ClientSecret"];

// Create HttpClient service
builder.Services.AddHttpClient("NokNok_D365Api", client => 
{
    client.BaseAddress = new Uri(d365Url);
    client.Timeout = TimeSpan.FromMinutes(10); // Increase timeout to 10 minutes

    // get token
    var authenticationService = new AuthenticationService(aadTenant, aadResource, aadClientAppId, aadClientAppSecret);
    var token = authenticationService.GetAuthenticationHeader();

    // add authentication to the header
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
});

builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("NokNok_D365Api"));

builder.Services.AddDbContext<SchedulerDbContext>(options =>
    options.UseSqlServer(connectionString, b => b.MigrationsAssembly("Application.Scheduler")));


builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(appConnectionString, b => b.MigrationsAssembly("Application")));

var statusConnectionString = builder.Configuration.GetConnectionString("StatusDbContext") ?? appConnectionString;
builder.Services.AddDbContext<StatusDbContext>(options =>
    options.UseSqlServer(statusConnectionString, b => b.MigrationsAssembly("Application")));

// Data Protection — MUST mirror the web app (same application name + key ring + DPAPI scope)
// so the scheduler can decrypt connection secrets the web app encrypted. AssetPingJob uses this
// to read stored DatabaseConnection passwords for read-only SELECT 1 / freshness probes.
var keysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(keysPath);
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("FlowByte.Application")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddSingleton<Application.Shared.Services.ICredentialProtector,
    Application.Shared.Services.CredentialProtector>();
builder.Services.AddScoped<Application.Shared.Services.IDatabaseTableService,
    Application.Shared.Services.DatabaseTableService>();
builder.Services.AddHttpClient();

// Scheduled ingestion: DuckDB access + the shared executor, plus the Hangfire jobs.
var duckdbOption = new DuckdbOption();
builder.Configuration.Bind("Duckdb", duckdbOption);
builder.Services.AddSingleton(duckdbOption);
// Surfaces config drift immediately: this must match the web app's Duckdb:DuckdbFilePath, or ingestion
// imports fail with "Dataset database not found".
Console.WriteLine($"[Scheduler] DuckDB file path: '{duckdbOption.DuckdbFilePath ?? "(null — Duckdb config missing!)"}'");
builder.Services.AddScoped<Application.Shared.Services.Data.IDuckdbService,
    Application.Shared.Services.Data.DuckdbService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IIngestionService,
    Application.Shared.Services.Data.IngestionService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IngestionJob>();
builder.Services.AddScoped<IngestionRegistrarJob>();

// Scheduled notebook runs. IDatasetService only needs ApplicationDbContext/DuckDB (both already
// registered above) so it's fully functional here; INotebookSharingService is NOT — see
// UnsupportedNotebookSharingService for why a stub is used instead of the real implementation.
builder.Services.AddScoped<Application.Shared.Services.Data.IDatasetService,
    Application.Shared.Services.Data.DatasetService>();
builder.Services.AddScoped<Application.Shared.Services.Data.INotebookSharingService,
    Application.Scheduler.Services.UnsupportedNotebookSharingService>();
builder.Services.AddSingleton<Application.Shared.Services.Data.INotebookRunCancellationRegistry,
    Application.Shared.Services.Data.NotebookRunCancellationRegistry>();
builder.Services.AddScoped<Application.Shared.Services.Data.IQueryNotebookService,
    Application.Shared.Services.Data.QueryNotebookService>();
builder.Services.AddScoped<Application.Shared.Services.Data.NotebookRunJob>();
builder.Services.AddScoped<NotebookRunRegistrarJob>();
builder.Services.Configure<Application.Shared.Options.NotebookOpsOptions>(builder.Configuration.GetSection("NotebookOps"));


builder.Services.AddHangfire(cfg => cfg
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseColouredConsoleLogProvider()
    .UseConsole()
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        SchemaName = "HangFire", // default; change if you want
        PrepareSchemaIfNecessary = true, // auto-create tables on first run
    }));


builder.Services.AddScoped<ISalesRepository, SalesRepository>();
builder.Services.AddScoped<IDatabaseRepository, DatabaseRepository>();
builder.Services.AddScoped<SalesJob>();
builder.Services.AddScoped<SalesSnapshotEmailJob>();
builder.Services.AddScoped<AssetPingJob>();

// Incident notification emails (Resend microservice) — used by AssetPingJob auto-incidents.
builder.Services.Configure<Application.Shared.Options.IncidentEmailOptions>(
    builder.Configuration.GetSection("IncidentNotificationEmail"));
builder.Services.AddScoped<Application.Shared.Services.IIncidentNotificationService,
    Application.Shared.Services.IncidentNotificationService>();
builder.Services.AddHttpClient(Application.Shared.Services.IncidentNotificationService.HttpClientName, (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<Application.Shared.Options.IncidentEmailOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.ApiBaseUri)) return;
    client.BaseAddress = new Uri(opts.ApiBaseUri);
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.Configure<SalesSnapshotEmailOptions>(builder.Configuration.GetSection("SalesSnapshotEmail"));

builder.Services.AddHttpClient("SalesSnapshotEmailApi", (sp, client) =>
{
    var emailOptions = sp.GetRequiredService<IOptions<SalesSnapshotEmailOptions>>().Value;
    if (string.IsNullOrWhiteSpace(emailOptions.ApiBaseUri))
    {
        return;
    }

    client.BaseAddress = new Uri(emailOptions.ApiBaseUri);
    client.Timeout = TimeSpan.FromSeconds(60);
});


// Queue routing (deployment split). Each machine runs THIS SAME binary but processes only the queues
// listed in its appsettings "Hangfire:Queues":
//   • Local server  → ["sales"]    : SalesJob / SalesSnapshotEmailJob (need on-prem RBO/store DBs).
//   • Cloud server  → ["default"]  : asset-ping, ingestion (DuckDB), stale-run sweep, and the batch
//                                    runs the web app enqueues.
// Jobs are tagged with [Queue("sales")]; everything else defaults to "default". A server never runs a
// job on a queue it doesn't list, so the split is enforced by storage, not by which process registered it.
var hangfireQueues = (builder.Configuration.GetSection("Hangfire:Queues").Get<string[]>() ?? new[] { "default" })
    .Select(q => q.Trim().ToLowerInvariant())
    .Where(q => q.Length > 0)
    .Distinct()
    .ToArray();
if (hangfireQueues.Length == 0) hangfireQueues = new[] { "default" };

var ownsDefaultQueue = hangfireQueues.Contains("default");
var ownsSalesQueue = hangfireQueues.Contains("sales");

Console.WriteLine($"[Scheduler] Hangfire queues for this server: {string.Join(", ", hangfireQueues)}");

builder.Services.AddHangfireServer(options =>
{
    options.Queues = hangfireQueues;
});

IConfiguration cfg = builder.Configuration;
var _salesUri = cfg.GetValue<string>("SalesApiUri");
builder.Services.AddScoped(http => new HttpClient { BaseAddress = new Uri(_salesUri) });


var app = builder.Build();

// 3) Dashboard (at /hangfire). Add auth if exposed publicly!
app.UseHangfireDashboard("/dashboard");




// Cron: every 10 minutes
var tz = GetTimeZone("Asia/Beirut"); // or null for server time
// #pragma warning disable CS0618 // Type or member is obsolete

List<Database> databases = new List<Database>();
List<Database> noknokDatabases = new List<Database>();

// Only the local "sales" server talks to the on-prem RBO/store databases. On the cloud server this load
// is unnecessary (and would fail to reach the on-prem network), so skip it.
if (ownsSalesQueue)
{
    using var scope = app.Services.CreateScope();
    var databaseRepository = scope.ServiceProvider.GetRequiredService<IDatabaseRepository>();
    databases = await databaseRepository.GetDatabaseDetails();
    Console.WriteLine($"Loaded {databases.Count} records at startup from RBO");
}




int offset = 0;
var minuteOffset = 0;


// NOTE: when re-enabling these sales recurring jobs, wrap them in `if (ownsSalesQueue) { ... }` so only
// the local server registers them (they're already [Queue("sales")]-tagged, so only it will execute them).
foreach (var db in databases)
{

    minuteOffset = offset % 5; // ensure it’s within 0-9
    //#pragma warning restore CS0618 // Type or member is obsolete
    RecurringJob.AddOrUpdate<SalesJob>(
        recurringJobId: $"sales-grouped-by-store-hour-{db.Name}",
        methodCall: job => job.RunAsync(db, null, CancellationToken.None), // context and ct are not used in this example
        cronExpression: $"{minuteOffset}/5 * * * *",
        timeZone: tz,
        queue: "sales" // Pin the recurring definition's queue; in Hangfire 1.8 this overrides the [Queue] attribute.
    );

    offset++;
}



// These sales recurring jobs are [Queue("sales")]-tagged, so their triggers enqueue into the "sales"
// queue. Only register them on a server that OWNS the sales queue — otherwise a default-only server
// keeps enqueuing them into a queue it never processes, so they pile up "enqueued but never run".
if (ownsSalesQueue)
{
    minuteOffset = 0; // ensure it’s within 0-9
    //#pragma warning restore CS0618 // Type or member is obsolete
    RecurringJob.AddOrUpdate<SalesJob>(
        recurringJobId: $"sales-grouped-by-store-hour_FO", //{db.Name}
        methodCall: job => job.RunNokNokFoAsync(null, CancellationToken.None), // context and ct are not used in this example
        cronExpression: $"{minuteOffset}/15 * * * *",
        timeZone: tz,
        queue: "sales" // Pin the recurring definition's queue; in Hangfire 1.8 this overrides the [Queue] attribute.
    );

    RecurringJob.AddOrUpdate<SalesSnapshotEmailJob>(
        recurringJobId: "sales-snapshot-email",
        methodCall: job => job.RunAsync(CancellationToken.None),
        cronExpression: "5 0 * * *",
        timeZone: tz,
        queue: "sales" // Without this, the recurring definition defaulted to "default" and the sales-only server never picked it up.
    );
}



// Recurring jobs below run on the "default" queue, so only the cloud server should register and execute
// them. Gating registration too (not just execution) keeps the local sales box from touching the app DB /
// DuckDB / on-prem probes it has no business running. If a single server owns both queues, it runs all.
if (ownsDefaultQueue)
{
    // Status ping monitoring — every 15 minutes
    RecurringJob.AddOrUpdate<AssetPingJob>(
        recurringJobId: "asset-ping-monitoring",
        methodCall: job => job.RunAsync(null, CancellationToken.None),
        cronExpression: "*/15 * * * *",
        timeZone: tz
    );

    // Scheduled ingestion: a registrar reconciles per-source recurring jobs against the ingestion_source
    // table every 5 minutes, so UI changes take effect without a scheduler restart.
    RecurringJob.AddOrUpdate<IngestionRegistrarJob>(
        recurringJobId: "ingestion-registrar",
        methodCall: job => job.RunAsync(null, CancellationToken.None),
        cronExpression: "*/5 * * * *",
        timeZone: tz
    );

    // Scheduled notebook runs: same reconcile-against-the-table pattern, against query_notebook's
    // schedule columns. The jobs it registers run on the "notebook" queue (see appsettings' Hangfire:Queues).
    RecurringJob.AddOrUpdate<NotebookRunRegistrarJob>(
        recurringJobId: "notebook-run-registrar",
        methodCall: job => job.RunAsync(null, CancellationToken.None),
        cronExpression: "*/5 * * * *",
        timeZone: tz
    );

    // Reconcile orphaned ingestion runs (stuck at "Running" after a process restart) on a recurring basis,
    // so stale rows are cleaned up even without a restart of either process.
    RecurringJob.AddOrUpdate<Application.Shared.Services.Data.IIngestionService>(
        recurringJobId: "ingestion-stale-run-sweep",
        methodCall: svc => svc.FailStaleRunsAsync(TimeSpan.FromHours(6), CancellationToken.None),
        cronExpression: "*/30 * * * *",
        timeZone: tz
    );

    // Reconcile once at startup so existing sources are scheduled immediately.
    using var scope = app.Services.CreateScope();
    try
    {
        var registrar = scope.ServiceProvider.GetRequiredService<IngestionRegistrarJob>();
        await registrar.RunAsync(null, CancellationToken.None);

        var ingestion = scope.ServiceProvider.GetRequiredService<Application.Shared.Services.Data.IIngestionService>();
        await ingestion.FailStaleRunsAsync(TimeSpan.FromHours(6), CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Initial ingestion reconcile failed: {ex.Message}");
    }

    try
    {
        var notebookRegistrar = scope.ServiceProvider.GetRequiredService<NotebookRunRegistrarJob>();
        await notebookRegistrar.RunAsync(null, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Initial notebook-run reconcile failed: {ex.Message}");
    }
}


app.Run();



static TimeZoneInfo? GetTimeZone(string id)
{
    try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
    catch
    { /* Windows/Linux name differences */
        try { return TimeZoneInfo.FindSystemTimeZoneById("Middle East Standard Time"); } // Windows for Beirut
        catch { return null; } // fall back to server local time
    }
}