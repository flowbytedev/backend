using Microsoft.AspNetCore.Authorization;

namespace Application.Shared.Authorization;

/// <summary>
/// Registers the per-company module-access policies. Called from both the server and the WASM
/// client so policy definitions are identical in both authorization pipelines.
/// </summary>
public static class FlowbyteAuthorizationPolicies
{
    public static void AddFlowbytePolicies(this AuthorizationOptions options)
    {
        Add(options, PolicyNames.DatasetsAccess, RoleSuffixes.Datasets);
        Add(options, PolicyNames.DataWarehouseRead, RoleSuffixes.DataWarehouse);
        Add(options, PolicyNames.MetricsRead, RoleSuffixes.MetricsRead, RoleSuffixes.MetricsWrite);
        Add(options, PolicyNames.MetricsWrite, RoleSuffixes.MetricsWrite);
        Add(options, PolicyNames.SalesRead, RoleSuffixes.Sales);
        Add(options, PolicyNames.StatusRead, RoleSuffixes.StatusRead, RoleSuffixes.Incidents);
        Add(options, PolicyNames.StatusWrite, RoleSuffixes.Incidents);
        Add(options, PolicyNames.InventoryRead, RoleSuffixes.InventoryRead);
        Add(options, PolicyNames.DashboardsRead, RoleSuffixes.DashboardsRead);
        Add(options, PolicyNames.DataLogRead, RoleSuffixes.DataAdmin);
    }

    private static void Add(AuthorizationOptions options, string policyName, params string[] suffixes)
    {
        options.AddPolicy(policyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new ModuleAccessRequirement(suffixes));
        });
    }
}
