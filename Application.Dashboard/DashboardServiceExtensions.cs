using Application.Dashboard.Configuration;
using Application.Dashboard.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Dashboard;

public static class DashboardServiceExtensions
{
    /// <summary>Registers the dashboard feature services (OOS dashboard + sales overview + dashboard/table links).</summary>
    public static IServiceCollection AddDashboard(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SalesDatasetSettings>(configuration.GetSection("SalesDataset"));
        services.AddScoped<IOosDashboardService, OosDashboardService>();
        services.AddScoped<ISalesOverviewService, SalesOverviewService>();
        services.AddScoped<IDailySalesService, DailySalesService>();
        services.AddScoped<IDashboardLinkService, DashboardLinkService>();
        return services;
    }
}
