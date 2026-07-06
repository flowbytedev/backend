using Application.Shared.Services.Logging;

namespace Application.Logging;

/// <summary>
/// Hosts the single background worker that drains the audit-log buffer into ClickHouse and ensures
/// the <c>data_app_log</c> schema exists at startup.
/// </summary>
public class DataAppLogHostedService : BackgroundService
{
    private readonly IDataAppLogService _log;
    private readonly ILogger<DataAppLogHostedService> _logger;

    public DataAppLogHostedService(IDataAppLogService log, ILogger<DataAppLogHostedService> logger)
    {
        _log = log;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_log.Enabled)
        {
            _logger.LogInformation("DataAppLog disabled or not configured; audit logging worker not started.");
            return;
        }

        await _log.InitializeAsync(stoppingToken);
        await _log.RunAsync(stoppingToken);
    }
}
