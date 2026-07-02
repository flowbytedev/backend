using Application.Shared.Models.Metrics;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services;

public interface IKpiDashboardService
{
    Task<List<KpiCardDto>> GetKpiCardsAsync(string companyId, CancellationToken ct = default);

    Task<List<KpiTrendPointDto>> GetKpiTrendAsync(string companyId, string department, string kpi, CancellationToken ct = default);
}

/// <summary>
/// Reads the warehouse <c>org.kpi</c> table (via <see cref="DataWarehouseDbContext"/>) and rolls each
/// (department, kpi) series up into a single dashboard card: current period, target, and the
/// prior-month / prior-year comparison values. Unit and direction are inferred from the KPI name
/// since the source table carries no metadata for either.
/// </summary>
public class KpiDashboardService : IKpiDashboardService
{
    private readonly DataWarehouseDbContext _context;

    public KpiDashboardService(DataWarehouseDbContext context)
    {
        _context = context;
    }

    private static readonly Dictionary<string, int> MonthMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6,
        ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12
    };

    public async Task<List<KpiCardDto>> GetKpiCardsAsync(string companyId, CancellationToken ct = default)
    {
        var rows = new List<(string Dept, string Kpi, int Year, int MonthNum, string Month, double Value, double Target)>();

        var connection = _context.Database.GetDbConnection();
        try
        {
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT department, kpi, [year], [month], [value], target FROM org.kpi WHERE company_id = @companyId";
            var p = cmd.CreateParameter();
            p.ParameterName = "@companyId";
            p.Value = companyId;
            cmd.Parameters.Add(p);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var dept = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var kpi = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(kpi)) continue;

                var year = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                var month = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var value = reader.IsDBNull(4) ? 0d : Convert.ToDouble(reader.GetValue(4));
                var target = reader.IsDBNull(5) ? 0d : Convert.ToDouble(reader.GetValue(5));

                var monthKey = month.Length >= 3 ? month.Substring(0, 3) : month;
                if (!MonthMap.TryGetValue(monthKey, out var monthNum)) continue;

                rows.Add((dept, kpi, year, monthNum, month, value, target));
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        var cards = new List<KpiCardDto>();
        foreach (var group in rows.GroupBy(r => new { r.Dept, r.Kpi }))
        {
            var latest = group.OrderBy(r => r.Year).ThenBy(r => r.MonthNum).Last();
            var byPeriod = group
                .GroupBy(r => (r.Year, r.MonthNum))
                .ToDictionary(g => g.Key, g => g.Last().Value);

            var pmYear = latest.MonthNum == 1 ? latest.Year - 1 : latest.Year;
            var pmMonth = latest.MonthNum == 1 ? 12 : latest.MonthNum - 1;

            double? prevMonth = byPeriod.TryGetValue((pmYear, pmMonth), out var pmVal) ? pmVal : null;
            double? prevYear = byPeriod.TryGetValue((latest.Year - 1, latest.MonthNum), out var pyVal) ? pyVal : null;

            cards.Add(new KpiCardDto
            {
                Department = group.Key.Dept,
                Kpi = group.Key.Kpi,
                Unit = InferUnit(group.Key.Kpi),
                Direction = InferDirection(group.Key.Kpi),
                Current = latest.Value,
                Target = latest.Target,
                PrevMonth = prevMonth,
                PrevYear = prevYear,
                Year = latest.Year,
                Month = latest.Month
            });
        }

        return cards
            .OrderBy(c => c.Department)
            .ThenBy(c => c.Kpi)
            .ToList();
    }

    private static readonly string[] MonthAbbrev =
        { "", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    public async Task<List<KpiTrendPointDto>> GetKpiTrendAsync(string companyId, string department, string kpi, CancellationToken ct = default)
    {
        var rows = new List<(int Year, int MonthNum, double Value, double Target)>();

        var connection = _context.Database.GetDbConnection();
        try
        {
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT [year], [month], [value], target FROM org.kpi " +
                              "WHERE company_id = @companyId AND kpi = @kpi AND ISNULL(department, '') = @department";
            AddParam(cmd, "@companyId", companyId);
            AddParam(cmd, "@kpi", kpi);
            AddParam(cmd, "@department", department ?? string.Empty);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var year = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                var month = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var value = reader.IsDBNull(2) ? 0d : Convert.ToDouble(reader.GetValue(2));
                var target = reader.IsDBNull(3) ? 0d : Convert.ToDouble(reader.GetValue(3));

                var monthKey = month.Length >= 3 ? month.Substring(0, 3) : month;
                if (!MonthMap.TryGetValue(monthKey, out var monthNum)) continue;

                rows.Add((year, monthNum, value, target));
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        if (rows.Count == 0) return new List<KpiTrendPointDto>();

        var byPeriod = rows
            .GroupBy(r => (r.Year, r.MonthNum))
            .ToDictionary(g => g.Key, g => g.Last());

        // Window = the latest year to date, plus the immediately preceding month (December of the prior year).
        var latest = rows.OrderBy(r => r.Year).ThenBy(r => r.MonthNum).Last();
        var window = new List<(int Year, int MonthNum)> { (latest.Year - 1, 12) };
        for (var m = 1; m <= latest.MonthNum; m++)
            window.Add((latest.Year, m));

        var points = new List<KpiTrendPointDto>();
        foreach (var (year, monthNum) in window)
        {
            if (!byPeriod.TryGetValue((year, monthNum), out var row)) continue;
            points.Add(new KpiTrendPointDto
            {
                Year = year,
                MonthNum = monthNum,
                Month = MonthAbbrev[monthNum],
                Label = $"{MonthAbbrev[monthNum]} '{(year % 100):D2}",
                Value = row.Value,
                Target = row.Target
            });
        }

        return points;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// The source table has no unit column, so we infer one from the KPI name: ratios/percentages/
    /// margins/rates → "%"; anything time-based → "days"; everything else → a plain number.
    /// </summary>
    private static string InferUnit(string kpi)
    {
        var name = kpi.ToLowerInvariant();
        if (name.Contains('%') || name.Contains("ratio") || name.Contains("margin")
            || name.Contains("contribution") || name.Contains("rate") || name.Contains("percent"))
            return "%";
        if (name.Contains("time") || name.Contains("days") || name.Contains("delivery"))
            return "days";
        return "num";
    }

    /// <summary>
    /// The source table has no "higher/lower is better" flag, so we infer it from the KPI name.
    /// Cost/loss/delay-type metrics are lower-is-better; everything else defaults to higher-is-better.
    /// </summary>
    private static string InferDirection(string kpi)
    {
        var name = kpi.ToLowerInvariant();
        string[] lowerIsBetter =
        {
            "wastage", "shrinkage", "return", "cost", "days", "delivery time", "turnover",
            "defect", "variance", "outstanding", "aging", "downtime", "complaint",
            "cycle time", "lead time", "response time", "in transit"
        };
        return lowerIsBetter.Any(k => name.Contains(k)) ? "down" : "up";
    }
}
