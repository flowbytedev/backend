using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.FluentUI.AspNetCore.Components;
using System.Collections.Generic;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using System.Net.Http.Json;
using System.Linq;
using System.Web;

namespace Application.Client.Pages.Data;

public partial class RealTimeSalesDashboard : IAsyncDisposable
{
    private HubConnection? hubConnection;
    private bool isLoading = true;
    private bool showColumnSettings = false;
    private DateTime? lastUpdated;

    [SupplyParameterFromQuery(Name = "c")]
    public string? companyId { get; set; }

    private SalesKpiData? overallKpi;
    private List<SalesBannerKpi>? bannerKpis;
    private List<SalesDashboardData>? dashboardData;
    private List<SalesDashboardData> storeCategorySnapshots = new();

    [Inject]
    public HttpClient HttpClient { get; set; } = default!;

    private ColumnSettings columnSettings = new()
    {
        ShowScheme = true,
        ShowStoreCode = true,
        ShowDivision = true,
        ShowCategory = true,
        ShowTotalSales = true,
        ShowTotalTransactions = true,
        ShowAverageBasket = true,
        ShowLastUpdated = true
    };

    ColumnResizeLabels resizeLabels = ColumnResizeLabels.Default with
    {
        DiscreteLabel = "Width (+/- 10px)",
        ResetAriaLabel = "Restore"
    };

    private DashboardGrouping currentGrouping = DashboardGrouping.Store;
    private List<SalesDashboardData> divisionData = new();
    private List<SalesDashboardData> categoryData = new();
    private List<SalesDashboardData> divisionCategoryData = new();

    private string filterText = string.Empty;
    private string sortColumn = "TotalSales";
    private bool sortAscending = false;

    private SalesDashboardData? selectedStore;
    private SalesDashboardData? selectedDivisionSummary;
    private SalesDashboardData? selectedCategorySummary;
    private List<SalesDashboardData> storeDivisionBreakdown = new();
    private List<SalesDashboardData> storeCategoryBreakdown = new();
    private bool showStoreInsights;

    private static readonly IReadOnlyList<(DashboardGrouping Value, string Label)> groupingOptions =
        new List<(DashboardGrouping, string)>
        {
            (DashboardGrouping.Store, "Stores"),
            (DashboardGrouping.Division, "Divisions"),
            (DashboardGrouping.Category, "Categories"),
            (DashboardGrouping.DivisionCategory, "Division + Category")
        };

    private const string UnknownDivisionLabel = "Unassigned Division";
    private const string UnknownCategoryLabel = "Unassigned Category";

    public bool IsConnected =>
        hubConnection?.State == HubConnectionState.Connected;

    protected override async Task OnInitializedAsync()
    {

        await InitializeSignalRConnection();
        await LoadInitialData();

        try
        {
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;
            
            if (user.Identity?.IsAuthenticated == true)
            {
                // Get company ID from query parameter or user claims
                var uri = new Uri(NavigationManager.Uri);
                //var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                //companyId = query["companyId"] ?? user.FindFirst("companyId")?.Value;

                if (!string.IsNullOrEmpty(companyId))
                {
                    await InitializeSignalRConnection();
                    await LoadInitialData();
                }
            }
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Error initializing dashboard: {ex.Message}");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task InitializeSignalRConnection()
    {
        try
        {
            Console.WriteLine("Initializing SignalR connection...");
            
            hubConnection = new HubConnectionBuilder()
                .WithUrl(NavigationManager.ToAbsoluteUri("/realtime/salesdata"))
                .Build();

            hubConnection.On<Notification<List<SalesData>>>("ReceiveSalesData", async (notification) =>
            {
                if (notification?.Data == null || notification.Data.Count == 0)
                {
                    return;
                }

                try
                {
                    Console.WriteLine($"Received SignalR batch: {notification.Data.Count} sales rows");

                    foreach (var storeGroup in notification.Data.GroupBy(d => new { d.Scheme, d.StoreCode }))
                    {
                        RemoveStoreSlices(storeGroup.Key.Scheme, storeGroup.Key.StoreCode);

                        foreach (var entry in storeGroup)
                        {
                            storeCategorySnapshots.Add(new SalesDashboardData
                            {
                                Scheme = entry.Scheme,
                                StoreCode = entry.StoreCode,
                                DivisionName = entry.DivisionName,
                                CategoryName = entry.CategoryName,
                                TotalSales = entry.NetAmountAcy,
                                TotalTransactions = entry.TotalTransactions,
                                TotalStoreTransactions = entry.TotalStoreTransactions.Value,
                                LastUpdated = entry.ReceivedAt
                            });
                        }
                    }

                    RebuildStoreAggregatesFromSnapshots();
                    RecalculateDashboardSummaries();
                    UpdateDerivedDataSets();

                    lastUpdated = DateTime.Now;

                    Console.WriteLine($"Updated dashboard: Stores={dashboardData?.Count ?? 0}, Categories={storeCategorySnapshots.Count}");

                    await InvokeAsync(StateHasChanged);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing SignalR data: {ex.Message}");
                    await InvokeAsync(() =>
                        ToastService.ShowError($"Error processing real-time data: {ex.Message}"));
                }
            });

            await hubConnection.StartAsync();
            Console.WriteLine("SignalR connection started successfully");

            if (!string.IsNullOrEmpty(companyId))
            {
                await hubConnection.InvokeAsync("JoinCompanyGroup", companyId);
                Console.WriteLine($"Joined company group: {companyId}");
            }

            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SignalR connection error: {ex.Message}");
            ToastService.ShowError($"Error connecting to real-time updates: {ex.Message}");
        }
    }


    private async Task LoadInitialData()
    {
        await LoadData();
        lastUpdated = DateTime.Now;
    }

    private async Task LoadData()
    {
        try
        {
            //if (string.IsNullOrEmpty(companyId)) return;

            HttpClient.DefaultRequestHeaders.Clear();
            HttpClient.DefaultRequestHeaders.Add("X-Company-ID", companyId);

            var kpiTask = await HttpClient.GetFromJsonAsync<SalesKpiData>("/api/salesdashboard/kpi");
            var bannerTask = await HttpClient.GetFromJsonAsync<List<SalesBannerKpi>>("/api/salesdashboard/banner-kpi");
            var dataTask = await HttpClient.GetFromJsonAsync<List<SalesDashboardData>>("/api/salesdashboard/data");

            //await Task.WhenAll(kpiTask, bannerTask, dataTask);

            overallKpi = kpiTask;
            bannerKpis = bannerTask;
            dashboardData = dataTask;
            storeCategorySnapshots = (dashboardData ?? new List<SalesDashboardData>())
                .Select(CloneDashboardRow)
                .ToList();
            RebuildStoreAggregatesFromSnapshots();
            RecalculateDashboardSummaries();
            UpdateDerivedDataSets();
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Error loading dashboard data: {ex.Message}");
        }
    }

    // Navigate to the historical Daily Sales dashboard, preserving the selected company (?c=).
    private void ViewHistory()
    {
        var target = "dashboards/daily-sales";
        if (!string.IsNullOrEmpty(companyId))
            target += $"?c={Uri.EscapeDataString(companyId)}";
        NavigationManager.NavigateTo(target);
    }

    private async Task RefreshData()
    {
        isLoading = true;
        StateHasChanged();

        try
        {
            await LoadData();
            lastUpdated = DateTime.Now;
            ToastService.ShowSuccess("Dashboard data refreshed");
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Error refreshing data: {ex.Message}");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private void ToggleColumnSettings()
    {
        showColumnSettings = !showColumnSettings;
        StateHasChanged();
    }

    private string GetGridTemplateColumns()
    {
        var columns = new List<string>();
        
        if (currentGrouping == DashboardGrouping.Store && columnSettings.ShowScheme) columns.Add("1fr");
        if (currentGrouping == DashboardGrouping.Store && columnSettings.ShowStoreCode) columns.Add("1fr");
        if ((currentGrouping == DashboardGrouping.Division || currentGrouping == DashboardGrouping.DivisionCategory) && columnSettings.ShowDivision)
            columns.Add("1fr");
        if ((currentGrouping == DashboardGrouping.Category || currentGrouping == DashboardGrouping.DivisionCategory) && columnSettings.ShowCategory)
            columns.Add("2fr");
        if (columnSettings.ShowTotalSales) columns.Add("120px");
        if (columnSettings.ShowTotalTransactions) columns.Add("120px");
        if (columnSettings.ShowAverageBasket) columns.Add("120px");
        if (columnSettings.ShowLastUpdated) columns.Add("100px");

        return string.Join(" ", columns);
    }

    private IEnumerable<SalesDashboardData> GetGridItems()
    {
        return currentGrouping switch
        {
            DashboardGrouping.Store => dashboardData ?? Enumerable.Empty<SalesDashboardData>(),
            DashboardGrouping.Division => divisionData,
            DashboardGrouping.Category => categoryData,
            DashboardGrouping.DivisionCategory => divisionCategoryData,
            _ => Enumerable.Empty<SalesDashboardData>()
        };
    }

    private IEnumerable<SalesDashboardData> GetFilteredAndSortedItems()
    {
        var items = GetGridItems();

        if (!string.IsNullOrWhiteSpace(filterText))
        {
            var term = filterText.Trim();
            items = items.Where(row =>
                MatchesFilter(row.Scheme, term) ||
                MatchesFilter(row.StoreCode, term) ||
                MatchesFilter(row.DivisionName, term) ||
                MatchesFilter(row.CategoryName, term));
        }

        return SortItems(items);
    }

    private static bool MatchesFilter(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<SalesDashboardData> SortItems(IEnumerable<SalesDashboardData> items) =>
        sortColumn switch
        {
            "Scheme" => Order(items, d => d.Scheme),
            "StoreCode" => Order(items, d => d.StoreCode),
            "DivisionName" => Order(items, d => d.DivisionName),
            "CategoryName" => Order(items, d => d.CategoryName),
            "TotalTransactions" => Order(items, d => currentGrouping == DashboardGrouping.Store ? d.TotalStoreTransactions : d.TotalTransactions),
            "AverageBasket" => Order(items, d => d.AverageBasket),
            "LastUpdated" => Order(items, d => d.LastUpdated),
            _ => Order(items, d => d.TotalSales)
        };

    private IOrderedEnumerable<SalesDashboardData> Order<TKey>(IEnumerable<SalesDashboardData> items, Func<SalesDashboardData, TKey> keySelector) =>
        sortAscending ? items.OrderBy(keySelector) : items.OrderByDescending(keySelector);

    private void SetSort(string column)
    {
        if (sortColumn == column)
        {
            sortAscending = !sortAscending;
        }
        else
        {
            sortColumn = column;
            sortAscending = true;
        }
    }

    private string SortIndicator(string column) =>
        sortColumn != column ? "" : (sortAscending ? "▲" : "▼");

    private void HandleGroupingChanged(ChangeEventArgs args)
    {
        if (args?.Value is null)
        {
            return;
        }

        if (Enum.TryParse(typeof(DashboardGrouping), args.Value.ToString(), out var parsed) &&
            parsed is DashboardGrouping grouping &&
            grouping != currentGrouping)
        {
            currentGrouping = grouping;
            StateHasChanged();
        }
    }

    private void UpdateDerivedDataSets()
    {
        var sourceData = storeCategorySnapshots.Any()
            ? storeCategorySnapshots
            : dashboardData ?? new List<SalesDashboardData>();

        if (!sourceData.Any())
        {
            divisionData = new List<SalesDashboardData>();
            categoryData = new List<SalesDashboardData>();
            divisionCategoryData = new List<SalesDashboardData>();
            return;
        }

        divisionData = sourceData
            .GroupBy(d => NormalizeDimension(d.DivisionName, UnknownDivisionLabel))
            .Select(g => new SalesDashboardData
            {
                DivisionName = g.Key,
                TotalSales = g.Sum(x => x.TotalSales),
                TotalTransactions = g.Sum(x => x.TotalTransactions),
                LastUpdated = g.Max(x => x.LastUpdated)
            })
            .OrderByDescending(d => d.TotalSales)
            .ToList();

        categoryData = sourceData
            .GroupBy(d => NormalizeDimension(d.CategoryName, UnknownCategoryLabel))
            .Select(g => new SalesDashboardData
            {
                CategoryName = g.Key,
                TotalSales = g.Sum(x => x.TotalSales),
                TotalTransactions = g.Sum(x => x.TotalTransactions),
                LastUpdated = g.Max(x => x.LastUpdated)
            })
            .OrderByDescending(d => d.TotalSales)
            .ToList();

        divisionCategoryData = sourceData
            .GroupBy(d => new
            {
                Division = NormalizeDimension(d.DivisionName, UnknownDivisionLabel),
                Category = NormalizeDimension(d.CategoryName, UnknownCategoryLabel)
            })
            .Select(g => new SalesDashboardData
            {
                DivisionName = g.Key.Division,
                CategoryName = g.Key.Category,
                TotalSales = g.Sum(x => x.TotalSales),
                TotalTransactions = g.Sum(x => x.TotalTransactions),
                LastUpdated = g.Max(x => x.LastUpdated)
            })
            .OrderByDescending(d => d.TotalSales)
            .ToList();

        RefreshStoreInsights();
    }

    private void OpenStoreInsights(SalesDashboardData store)
    {
        BuildStoreInsights(store);
        showStoreInsights = true;
    }

    private void RefreshStoreInsights()
    {
        if (!showStoreInsights || selectedStore is null)
        {
            return;
        }

        BuildStoreInsights(selectedStore);
    }

    private void BuildStoreInsights(SalesDashboardData store)
    {
        var latestStore = FindStoreMatch(store);
        selectedStore = latestStore ?? store;

        var storeSlices = storeCategorySnapshots
            .Where(s => string.Equals(s.Scheme, selectedStore?.Scheme, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.StoreCode, selectedStore?.StoreCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!storeSlices.Any() && selectedStore is not null)
        {
            storeSlices.Add(CloneDashboardRow(selectedStore));
        }

        var divisionGroupsForStore = storeSlices
            .GroupBy(s => NormalizeDimension(s.DivisionName, UnknownDivisionLabel))
            .Select(g => new SalesDashboardData
            {
                DivisionName = g.Key,
                TotalSales = g.Sum(x => x.TotalSales),
                TotalTransactions = g.Sum(x => x.TotalTransactions),
                LastUpdated = g.Max(x => x.LastUpdated)
            })
            .OrderByDescending(d => d.TotalSales)
            .ToList();

        selectedDivisionSummary = divisionGroupsForStore.FirstOrDefault() ?? new SalesDashboardData
        {
            DivisionName = UnknownDivisionLabel,
            TotalSales = selectedStore?.TotalSales ?? 0,
            TotalTransactions = selectedStore?.TotalTransactions ?? 0,
            LastUpdated = selectedStore?.LastUpdated ?? DateTime.Now
        };

        var focusDivisionKey = NormalizeDimension(selectedDivisionSummary?.DivisionName, UnknownDivisionLabel);

        storeDivisionBreakdown = storeSlices
            .Where(s => string.Equals(NormalizeDimension(s.DivisionName, UnknownDivisionLabel), focusDivisionKey, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => NormalizeDimension(s.CategoryName, UnknownCategoryLabel))
            .Select(g => new SalesDashboardData
            {
                CategoryName = g.Key,
                TotalSales = g.Sum(x => x.TotalSales),
                TotalTransactions = g.Sum(x => x.TotalTransactions),
                LastUpdated = g.Max(x => x.LastUpdated)
            })
            .OrderByDescending(d => d.TotalSales)
            .ToList();

        var categoryGroupsForStore = storeSlices
            .GroupBy(s => NormalizeDimension(s.CategoryName, UnknownCategoryLabel))
            .Select(g => new SalesDashboardData
            {
                CategoryName = g.Key,
                DivisionName = NormalizeDimension(g.First().DivisionName, UnknownDivisionLabel),
                TotalSales = g.Sum(x => x.TotalSales),
                TotalTransactions = g.Sum(x => x.TotalTransactions),
                LastUpdated = g.Max(x => x.LastUpdated)
            })
            .OrderByDescending(d => d.TotalSales)
            .ToList();

        selectedCategorySummary = categoryGroupsForStore.FirstOrDefault() ?? new SalesDashboardData
        {
            CategoryName = UnknownCategoryLabel,
            DivisionName = selectedDivisionSummary?.DivisionName ?? UnknownDivisionLabel,
            TotalSales = selectedStore?.TotalSales ?? 0,
            TotalTransactions = selectedStore?.TotalTransactions ?? 0,
            LastUpdated = selectedStore?.LastUpdated ?? DateTime.Now
        };

        storeCategoryBreakdown = categoryGroupsForStore;
    }

    private SalesDashboardData? FindStoreMatch(SalesDashboardData store)
    {
        if (dashboardData is null)
        {
            return null;
        }

        return dashboardData.FirstOrDefault(d =>
            string.Equals(d.Scheme, store.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.StoreCode, store.StoreCode, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDimension(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FormatCurrency(decimal? value) =>
        value.HasValue ? value.Value.ToString("C") : "--";

    private static string FormatNumber(int? value) =>
        value.HasValue ? value.Value.ToString("N0") : "--";

    private void RemoveStoreSlices(string? scheme, string? storeCode)
    {
        storeCategorySnapshots.RemoveAll(s =>
            string.Equals(s.Scheme, scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase));
    }

    private void RebuildStoreAggregatesFromSnapshots()
    {
        if (!storeCategorySnapshots.Any())
        {
            dashboardData = new List<SalesDashboardData>();
            return;
        }

        dashboardData = storeCategorySnapshots
            .GroupBy(s => new { s.Scheme, s.StoreCode })
            .Select(g => new SalesDashboardData
            {
                Scheme = g.Key.Scheme,
                StoreCode = g.Key.StoreCode,
                TotalSales = g.Sum(x => x.TotalSales),
                TotalTransactions = g.Sum(x => x.TotalTransactions),
                TotalStoreTransactions = g.Sum(x => x.TotalStoreTransactions),
                LastUpdated = g.Max(x => x.LastUpdated)
            })
            .OrderByDescending(d => d.TotalSales)
            .ToList();
    }

    private void RecalculateDashboardSummaries()
    {
        if (dashboardData == null)
        {
            dashboardData = new List<SalesDashboardData>();
        }

        overallKpi ??= new SalesKpiData();

        overallKpi.TotalSales = dashboardData.Sum(d => d.TotalSales);
        overallKpi.TotalTransactions = dashboardData.Sum(d => Convert.ToInt32(d.TotalStoreTransactions));
        overallKpi.TotalStores = dashboardData.Count;
        overallKpi.TotalSchemes = dashboardData.Select(d => d.Scheme).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        overallKpi.LastUpdated = DateTime.Now;

        bannerKpis = dashboardData
            .GroupBy(d => d.Scheme ?? "Unknown")
            .Select(g => new SalesBannerKpi
            {
                Banner = g.Key,
                TotalSales = g.Sum(x => x.TotalSales),
                TotalTransactions = Convert.ToInt32(g.Sum(x => x.TotalStoreTransactions)),
                StoreCount = g.Count(),
                LastUpdated = g.Max(x => x.LastUpdated)
            })
            .OrderByDescending(b => b.TotalSales)
            .ToList();
    }

    private static SalesDashboardData CloneDashboardRow(SalesDashboardData source) =>
        new()
        {
            Scheme = source.Scheme,
            StoreCode = source.StoreCode,
            DivisionName = source.DivisionName,
            CategoryName = source.CategoryName,
            TotalSales = source.TotalSales,
            TotalTransactions = source.TotalTransactions,
            TotalStoreTransactions = source.TotalStoreTransactions,
            LastUpdated = source.LastUpdated
        };

    private void CloseStoreInsights()
    {
        showStoreInsights = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (hubConnection is not null)
        {
            try
            {
                if (!string.IsNullOrEmpty(companyId))
                {
                    await hubConnection.InvokeAsync("LeaveCompanyGroup", companyId);
                }
            }
            catch (Exception)
            {
                // Ignore errors when leaving group during disposal
            }

            await hubConnection.DisposeAsync();
        }
    }

    public class ColumnSettings
    {
        public bool ShowScheme { get; set; } = true;
        public bool ShowStoreCode { get; set; } = true;
        public bool ShowDivision { get; set; } = true;
        public bool ShowCategory { get; set; } = true;
        public bool ShowTotalSales { get; set; } = true;
        public bool ShowTotalTransactions { get; set; } = true;
        public bool ShowAverageBasket { get; set; } = true;
        public bool ShowLastUpdated { get; set; } = true;
    }

    private enum DashboardGrouping
    {
        Store,
        Division,
        Category,
        DivisionCategory
    }
}
