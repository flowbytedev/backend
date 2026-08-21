using Application.Shared.Data;
using Application.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Application.Shared.Services;

/// <summary>
/// Access checks against the shared `identity` database. This app never writes those tables --
/// the identity app owns them -- so every query here is AsNoTracking.
///
/// Uses IDbContextFactory rather than the scoped context: the launcher and the per-request gate
/// can both fire while a page is mid-render, and sharing one context across concurrent queries
/// throws "A second operation was started on this context instance".
/// </summary>
public class ApplicationAccessService : IApplicationAccessService
{
    private readonly IDbContextFactory<UserManagementDbContext> _contextFactory;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApplicationAccessService> _logger;

    public ApplicationAccessService(
        IDbContextFactory<UserManagementDbContext> contextFactory,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<ApplicationAccessService> logger)
    {
        _contextFactory = contextFactory;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    public string? ApplicationId => _configuration["ApplicationId"];

    private bool GateEnabled => ReadBool("ApplicationAccess:Enabled", true);

    /// <summary>
    /// How long a positive decision is trusted. This is the worst-case delay between an admin
    /// revoking access in the identity app and this app noticing: the cache lives in this process
    /// and nothing external can invalidate it.
    /// </summary>
    private TimeSpan AllowTtl => TimeSpan.FromSeconds(ReadInt("ApplicationAccess:CacheSeconds", 60));

    /// <summary>Shorter, so a freshly granted user does not wait out the full allow TTL.</summary>
    private TimeSpan DenyTtl => TimeSpan.FromSeconds(ReadInt("ApplicationAccess:DenyCacheSeconds", 15));

    /// <summary>The application row changes rarely; keep it off the hot path.</summary>
    private TimeSpan AppTtl => TimeSpan.FromSeconds(ReadInt("ApplicationAccess:AppCacheSeconds", 300));

    // Parsed by hand rather than with IConfiguration.GetValue<T>: that extension lives in
    // Microsoft.Extensions.Configuration.Binder, which relay's Application.Shared does not
    // reference, and this machine cannot restore new packages.
    private bool ReadBool(string key, bool fallback)
        => bool.TryParse(_configuration[key], out var value) ? value : fallback;

    private int ReadInt(string key, int fallback)
        => int.TryParse(_configuration[key], out var value) && value > 0 ? value : fallback;

    public async Task<List<AppTile>> GetAppTilesForUserAsync(string applicationUserId)
    {
        if (string.IsNullOrWhiteSpace(applicationUserId))
        {
            return new List<AppTile>();
        }

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Soft deletes are filtered on BOTH sides: revoking a grant and retiring an
            // application are separate actions, and either must hide the tile. An app with no URL
            // is still returned -- the launcher renders it greyed out and unclickable, so a
            // half-configured app is visible to whoever can fix it rather than silently missing.
            var tiles = await context.AppRegistryUserAccess
                .AsNoTracking()
                .Where(access => access.ApplicationUserId == applicationUserId && !access.IsDeleted)
                .Join(context.AppRegistryApplication
                             .Where(app => !app.IsDeleted && app.IsActive),
                      access => access.ApplicationId,
                      app => app.Id,
                      (access, app) => app)
                .OrderBy(app => app.DisplayOrder)
                .ThenBy(app => app.Name)
                .Select(app => new AppTile
                {
                    Id = app.Id,
                    Name = app.Name ?? app.Id,
                    Url = app.Url,
                    Icon = app.Icon,
                    Color = app.Color,
                    Description = app.Description
                })
                .ToListAsync();

            return tiles;
        }
        catch (Exception ex)
        {
            // An empty launcher is a much smaller problem than a broken header.
            _logger.LogError(ex, "Could not load app launcher tiles for {UserId}.", applicationUserId);
            return new List<AppTile>();
        }
    }

    public async Task<ApplicationAccessDecision> EvaluateAsync(
        string applicationUserId,
        CancellationToken cancellationToken = default)
    {
        if (!GateEnabled)
        {
            return ApplicationAccessDecision.Indeterminate;
        }

        var applicationId = ApplicationId;

        if (string.IsNullOrWhiteSpace(applicationId))
        {
            // appsettings.json is gitignored in every one of these repos, so a fresh environment
            // or a new deployment slot can easily arrive without this key. Failing closed here
            // would brick the app for everyone with no way in except a redeploy.
            _logger.LogError(
                "ApplicationId is not configured -- the per-application access gate is DISABLED and everyone is being let through.");
            return ApplicationAccessDecision.Indeterminate;
        }

        if (string.IsNullOrWhiteSpace(applicationUserId))
        {
            return ApplicationAccessDecision.Denied;
        }

        var grantKey = $"appaccess:{applicationId}:{applicationUserId}";
        if (_cache.TryGetValue<ApplicationAccessDecision>(grantKey, out var cached))
        {
            return cached;
        }

        ApplicationAccessDecision decision;
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var appState = await GetApplicationStateAsync(context, applicationId, cancellationToken);

            if (appState is null)
            {
                // No row for the configured id: a typo, or an environment where 001/002 have not
                // been run. Loud, repeated, and fail-open -- see the comment above.
                _logger.LogError(
                    "No dbo.application row for id {ApplicationId} -- the access gate is failing open.",
                    applicationId);
                return ApplicationAccessDecision.Indeterminate;
            }

            if (!appState.Value.IsLive)
            {
                // Retiring an app is a deliberate admin action, so this one fails closed.
                decision = ApplicationAccessDecision.Denied;
            }
            else
            {
                var granted = await context.AppRegistryUserAccess
                    .AsNoTracking()
                    .AnyAsync(access => access.ApplicationUserId == applicationUserId
                                        && access.ApplicationId == applicationId
                                        && !access.IsDeleted,
                              cancellationToken);

                decision = granted ? ApplicationAccessDecision.Allowed : ApplicationAccessDecision.Denied;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Signing in already depends on this database, so if it is down the user cannot get
            // here anyway. Do not turn a transient SQL fault into a mass logout.
            _logger.LogError(ex,
                "Access check failed for {UserId} on {ApplicationId}; failing open.",
                applicationUserId, applicationId);
            return ApplicationAccessDecision.Indeterminate;
        }

        _cache.Set(grantKey, decision,
            decision == ApplicationAccessDecision.Allowed ? AllowTtl : DenyTtl);

        return decision;
    }

    public async Task<bool> HasAccessAsync(string applicationUserId, CancellationToken cancellationToken = default)
        => await EvaluateAsync(applicationUserId, cancellationToken) != ApplicationAccessDecision.Denied;

    public async Task<string> GetApplicationNameAsync(CancellationToken cancellationToken = default)
    {
        var applicationId = ApplicationId;
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return "this application";
        }

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var state = await GetApplicationStateAsync(context, applicationId, cancellationToken);
            return string.IsNullOrWhiteSpace(state?.Name) ? applicationId : state!.Value.Name!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the name of application {ApplicationId}.", applicationId);
            return applicationId;
        }
    }

    public void Invalidate(string applicationUserId)
    {
        var applicationId = ApplicationId;
        if (!string.IsNullOrWhiteSpace(applicationId) && !string.IsNullOrWhiteSpace(applicationUserId))
        {
            _cache.Remove($"appaccess:{applicationId}:{applicationUserId}");
        }
    }

    private async Task<(bool IsLive, string? Name)?> GetApplicationStateAsync(
        UserManagementDbContext context,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var key = $"appmeta:{applicationId}";
        if (_cache.TryGetValue<(bool IsLive, string? Name)?>(key, out var cached))
        {
            return cached;
        }

        var row = await context.AppRegistryApplication
            .AsNoTracking()
            .Where(app => app.Id == applicationId)
            .Select(app => new { app.Name, app.IsDeleted, app.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        (bool IsLive, string? Name)? state =
            row is null ? null : (!row.IsDeleted && row.IsActive, row.Name);

        _cache.Set(key, state, AppTtl);
        return state;
    }

}
