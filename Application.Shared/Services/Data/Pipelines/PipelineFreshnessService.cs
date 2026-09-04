using Application.Shared.Data;

using Application.Shared.Models.Data.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Decides, per step, whether a pipeline's data is as recent as it was declared to be.
/// <para>
/// <b>Every node, not just the destinations.</b> A destination that is late is a symptom; the useful answer
/// is which step stopped. Within one pipeline an ancestor can never be staler than its descendant — a
/// partial run's closure forces ancestors to execute — so the interesting shape is not "who is late" but
/// "who is the earliest one late", which is what <see cref="PipelineFreshnessVerdict.IsRootCause"/> marks.
/// </para>
/// <para>
/// <b>What this cannot see.</b> A source step succeeding proves the read worked, not that the data moved.
/// A <c>source.dataset</c> pointed at a table another pipeline stopped filling looks perfectly fresh here,
/// because from this graph's point of view it is. Detecting that needs a fingerprint of the source itself,
/// which is a separate mechanism — see the source-freshness section of <c>docs/PIPELINES.md</c>.
/// </para>
/// </summary>
public interface IPipelineFreshnessService
{
    /// <summary>
    /// Read-only verdicts for one pipeline. Never writes, so it is safe on the request path — the sweep is
    /// the only thing that persists anything.
    /// </summary>
    Task<PipelineFreshnessReport?> EvaluateAsync(
        string companyId, string pipelineId, CancellationToken ct = default);

    /// <summary>
    /// Every enabled pipeline, across companies, for the maintenance sweep. Also seeds missing durable
    /// rows from surviving step history — see <see cref="PipelineNodeFreshness"/> on why the durable copy
    /// exists at all.
    /// </summary>
    Task<List<PipelineFreshnessReport>> SweepAsync(CancellationToken ct = default);

    /// <summary>
    /// Records the sweep's verdict so the next pass can tell a new violation from a continuing one.
    /// Returns the nodes that changed state.
    /// </summary>
    Task<List<PipelineFreshnessVerdict>> CommitAlertStateAsync(
        PipelineFreshnessReport report, CancellationToken ct = default);
}

public class PipelineFreshnessService(
    ApplicationDbContext db,
    ICompanySettingsService companySettings) : IPipelineFreshnessService
{
    public async Task<PipelineFreshnessReport?> EvaluateAsync(
        string companyId, string pipelineId, CancellationToken ct = default)
    {
        var pipeline = await db.Pipeline.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == pipelineId, ct);

        if (pipeline is null) return null;

        var recorded = await LoadRecordedAsync(companyId, new[] { pipelineId }, ct);
        return Judge(pipeline, recorded.GetValueOrDefault(pipelineId), DateTime.UtcNow);
    }

    public async Task<List<PipelineFreshnessReport>> SweepAsync(CancellationToken ct = default)
    {
        // Disabled pipelines are excluded: an operator who turned one off has already said it is not
        // expected to run, and alerting that it has not run is noise they cannot act on.
        var pipelines = await db.Pipeline.AsNoTracking()
            .Where(p => p.IsEnabled)
            .ToListAsync(ct);

        if (pipelines.Count == 0) return new();

        var now = DateTime.UtcNow;
        var reports = new List<PipelineFreshnessReport>(pipelines.Count);

        foreach (var group in pipelines.GroupBy(p => p.CompanyId, StringComparer.Ordinal))
        {
            // Per company, because the toggle lives on that company's settings row. A company that has
            // turned checks off is skipped entirely rather than evaluated-and-not-alerted: the sweep's
            // writes are what drive alerting, and recording verdicts nobody asked for would mean the
            // toggle silently changed what is stored as well as what is sent.
            var settings = await companySettings.GetFreshnessAsync(group.Key ?? string.Empty, ct);
            if (settings.Enabled == false) continue;

            var ids = group.Select(p => p.Id).ToList();
            var recorded = await LoadRecordedAsync(group.Key, ids, ct);

            // Bootstrap: a pipeline that predates this table has no durable rows, and reporting every one
            // of its nodes as never-run would open an alert per node on the deploy that shipped the
            // feature. Surviving step rows give the real answer, and seeding from them is a one-off that
            // self-heals as runs happen.
            await SeedFromStepHistoryAsync(group.Key, ids, recorded, ct);

            // One instant for the whole sweep, passed down rather than re-read per pipeline: two nodes
            // either side of a deadline must not disagree about which side they are on in one pass.
            foreach (var pipeline in group)
                reports.Add(Judge(pipeline, recorded.GetValueOrDefault(pipeline.Id), now));
        }

        return reports;
    }

    /// <summary>Projects the stored rows down to what the evaluator needs, and hands off the decision.</summary>
    private static PipelineFreshnessReport Judge(
        Pipeline pipeline, Dictionary<string, PipelineNodeFreshness>? recorded, DateTime utcNow)
    {
        var lastSuccess = recorded is null
            ? new Dictionary<string, DateTime?>(StringComparer.Ordinal)
            : recorded.ToDictionary(x => x.Key, x => x.Value.LastSuccessAt, StringComparer.Ordinal);

        return PipelineFreshnessEvaluator.Evaluate(
            pipeline.Id, pipeline.Name, pipeline.CompanyId,
            pipeline.GraphJson, pipeline.TimeZone, lastSuccess, utcNow);
    }

    public async Task<List<PipelineFreshnessVerdict>> CommitAlertStateAsync(
        PipelineFreshnessReport report, CancellationToken ct = default)
    {
        var changed = new List<PipelineFreshnessVerdict>();

        var rows = await db.PipelineNodeFreshness
            .Where(x => x.CompanyId == report.CompanyId && x.PipelineId == report.PipelineId)
            .ToListAsync(ct);

        foreach (var verdict in report.Nodes)
        {
            // Unchecked nodes are left alone entirely. Writing a row for one would create state for a node
            // nobody asked to monitor, and it would then have to be cleaned up if a policy never arrives.
            if (verdict.Status == PipelineFreshnessStatus.Unchecked) continue;

            var row = rows.FirstOrDefault(x => x.NodeId == verdict.NodeId);

            if (row is null)
            {
                row = new PipelineNodeFreshness
                {
                    CompanyId = report.CompanyId,
                    PipelineId = report.PipelineId,
                    NodeId = verdict.NodeId,
                    LastSuccessAt = verdict.LastSuccessAt,
                    CreatedOn = DateTime.Now
                };
                db.PipelineNodeFreshness.Add(row);
            }

            if (!string.Equals(row.AlertedStatus, verdict.Status, StringComparison.Ordinal))
            {
                // The transition, not the state, is the alert. A pipeline that has been stale for a week
                // must not re-notify on every five-minute pass.
                row.AlertedStatus = verdict.Status;
                row.AlertedAt = DateTime.UtcNow;
                row.ModifiedOn = DateTime.Now;
                changed.Add(verdict);
            }
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }

    // ------------------------------------------------------------------ persistence

    private async Task<Dictionary<string, Dictionary<string, PipelineNodeFreshness>>> LoadRecordedAsync(
        string? companyId, IReadOnlyCollection<string> pipelineIds, CancellationToken ct)
    {
        var rows = await db.PipelineNodeFreshness.AsNoTracking()
            .Where(x => x.CompanyId == companyId && pipelineIds.Contains(x.PipelineId))
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.PipelineId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(x => x.NodeId, x => x, StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Fills gaps in the durable table from step rows that have not yet been purged, and persists what it
    /// finds. Mutates <paramref name="recorded"/> so the caller's evaluation sees the seeded values.
    /// </summary>
    private async Task SeedFromStepHistoryAsync(
        string? companyId, IReadOnlyCollection<string> pipelineIds,
        Dictionary<string, Dictionary<string, PipelineNodeFreshness>> recorded,
        CancellationToken ct)
    {
        var haveRows = pipelineIds
            .Where(id => recorded.ContainsKey(id) && recorded[id].Count > 0)
            .ToHashSet(StringComparer.Ordinal);

        var missing = pipelineIds.Where(id => !haveRows.Contains(id)).ToList();
        if (missing.Count == 0) return;

        var history = await db.PipelineRunStep.AsNoTracking()
            .Where(s => s.CompanyId == companyId
                        && s.PipelineId != null && missing.Contains(s.PipelineId)
                        && s.Status == PipelineStepStatus.Success
                        && s.CompletedAt != null)
            .GroupBy(s => new { s.PipelineId, s.NodeId })
            .Select(g => new
            {
                g.Key.PipelineId,
                g.Key.NodeId,
                LastSuccessAt = g.Max(s => s.CompletedAt)
            })
            .ToListAsync(ct);

        if (history.Count == 0) return;

        foreach (var item in history)
        {
            var row = new PipelineNodeFreshness
            {
                CompanyId = companyId,
                PipelineId = item.PipelineId!,
                NodeId = item.NodeId,
                LastSuccessAt = item.LastSuccessAt,
                CreatedOn = DateTime.Now
            };

            db.PipelineNodeFreshness.Add(row);

            if (!recorded.TryGetValue(item.PipelineId!, out var byNode))
                recorded[item.PipelineId!] = byNode = new(StringComparer.Ordinal);

            byNode[item.NodeId] = row;
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Freshness for one pipeline, node by node.</summary>
public sealed class PipelineFreshnessReport
{
    public string? CompanyId { get; set; }
    public string PipelineId { get; set; } = string.Empty;
    public string? PipelineName { get; set; }

    /// <summary>Worst node verdict, by <see cref="PipelineFreshnessStatus.Rank"/>.</summary>
    public string Status { get; set; } = PipelineFreshnessStatus.Unchecked;

    /// <summary>One line naming the earliest cause, for a list row or an alert subject.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>First independent cause in dependency order. Null when nothing is late.</summary>
    public string? RootCauseNodeId { get; set; }

    public DateTime EvaluatedAt { get; set; }

    public List<PipelineFreshnessVerdict> Nodes { get; set; } = new();

    public bool HasViolation => PipelineFreshnessStatus.IsViolation(Status);
}

/// <summary>One step's verdict.</summary>
public sealed class PipelineFreshnessVerdict
{
    public string NodeId { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? NodeType { get; set; }

    public string Status { get; set; } = PipelineFreshnessStatus.Unchecked;

    /// <summary>Plain-language explanation, shown as-is in the UI and in alerts.</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTime? LastSuccessAt { get; set; }

    /// <summary>The instant this step had to have succeeded by. Null when nothing is asserted.</summary>
    public DateTime? DueBy { get; set; }

    public long? LagMinutes { get; set; }

    /// <summary>
    /// True when this step is late and no step upstream of it is. These are the ones worth looking at;
    /// the rest are downstream of one of them.
    /// </summary>
    public bool IsRootCause { get; set; }
}
