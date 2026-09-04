using Application.Shared.Models;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// The freshness decision, as a pure function of the graph, what was recorded, and the clock.
/// <para>
/// Split out from <see cref="PipelineFreshnessService"/> so the judgement has no database in it. That
/// matters for more than tidiness: one sweep must judge every pipeline against a single instant, or two
/// nodes either side of a deadline could disagree about which side they are on within one pass — and the
/// same code has to answer an API request and a sweep identically, or the page and the alert will contradict
/// each other.
/// </para>
/// </summary>
public static class PipelineFreshnessEvaluator
{
    /// <param name="lastSuccessByNode">
    /// When each node last produced its output. A node absent from the map, or present with null, has never
    /// succeeded — which <see cref="PipelineFreshnessStatus.Never"/> reports distinctly from being late.
    /// </param>
    public static PipelineFreshnessReport Evaluate(
        string pipelineId, string? pipelineName, string? companyId,
        string? graphJson, string? scheduleTimeZone,
        IReadOnlyDictionary<string, DateTime?> lastSuccessByNode,
        DateTime utcNow)
    {
        var report = new PipelineFreshnessReport
        {
            CompanyId = companyId,
            PipelineId = pipelineId,
            PipelineName = pipelineName,
            EvaluatedAt = utcNow,
            Status = PipelineFreshnessStatus.Unchecked,
            Summary = "This pipeline has no freshness policy."
        };

        var graph = PipelineGraph.TryParse(graphJson);

        if (graph is null || graph.Nodes.Count == 0)
        {
            report.Summary = "This pipeline has no readable graph, so there is nothing to check.";
            return report;
        }

        var defaultZone = ScheduleTimeZones.Resolve(scheduleTimeZone) ?? ScheduleTimeZones.Default;

        foreach (var nodeId in TopologicalOrder(graph))
        {
            var node = graph.Node(nodeId);
            if (node is null) continue;

            var policy = node.Freshness ?? graph.Settings.Freshness;
            lastSuccessByNode.TryGetValue(nodeId, out var lastSuccess);

            report.Nodes.Add(Judge(node, policy, lastSuccess, defaultZone, utcNow));
        }

        // A violation whose ancestor also violates is a consequence of it, not an independent fault. Marked
        // after the pass because it needs every verdict, and per node rather than as a single root so a
        // fan-in with two broken branches names both.
        var violating = report.Nodes
            .Where(n => PipelineFreshnessStatus.IsViolation(n.Status))
            .Select(n => n.NodeId)
            .ToHashSet(StringComparer.Ordinal);

        if (violating.Count > 0)
        {
            var ancestors = AncestorMap(graph);

            foreach (var verdict in report.Nodes.Where(n => violating.Contains(n.NodeId)))
            {
                var upstream = ancestors.GetValueOrDefault(verdict.NodeId);
                verdict.IsRootCause = upstream is null || !upstream.Any(violating.Contains);
            }
        }

        report.Status = report.Nodes.Count == 0
            ? PipelineFreshnessStatus.Unchecked
            : report.Nodes.MaxBy(n => PipelineFreshnessStatus.Rank(n.Status))!.Status;

        report.RootCauseNodeId = report.Nodes.FirstOrDefault(n => n.IsRootCause)?.NodeId;
        report.Summary = Summarize(report);

        return report;
    }

    private static PipelineFreshnessVerdict Judge(
        PipelineNodeDef node, PipelineFreshnessPolicy? policy,
        DateTime? lastSuccess, TimeZoneInfo? defaultZone, DateTime utcNow)
    {
        var verdict = new PipelineFreshnessVerdict
        {
            NodeId = node.Id,
            Label = node.Label,
            NodeType = node.Type,
            LastSuccessAt = lastSuccess
        };

        if (policy is null || !policy.IsActive)
        {
            verdict.Status = PipelineFreshnessStatus.Unchecked;
            verdict.Reason = "No freshness policy applies to this step.";
            return verdict;
        }

        if (policy.Validate() is { } invalid)
        {
            verdict.Status = PipelineFreshnessStatus.Unknown;
            verdict.Reason = invalid;
            return verdict;
        }

        DateTime dueBy;

        if (policy.MaxLagMinutes is { } lag)
        {
            dueBy = utcNow.AddMinutes(-lag);
        }
        else
        {
            var zone = policy.TimeZone is null ? defaultZone : ScheduleTimeZones.Resolve(policy.TimeZone);
            var tick = CronDeadline.MostRecent(policy.Cron, zone, utcNow);

            if (tick is null)
            {
                verdict.Status = PipelineFreshnessStatus.Unknown;
                verdict.Reason = $"'{policy.Cron}' has no occurrence this server can find within the last "
                               + "400 days, so no deadline could be worked out.";
                return verdict;
            }

            // Inside the grace window the latest deadline is not yet binding, so the one that must have been
            // met is the previous occurrence. Without this, every pipeline is reported stale at exactly the
            // moment it is supposed to be running.
            if (utcNow < tick.Value.AddMinutes(policy.GraceMinutes))
            {
                var previous = CronDeadline.MostRecent(policy.Cron, zone, tick.Value.AddMinutes(-1));

                if (previous is null)
                {
                    verdict.Status = PipelineFreshnessStatus.Fresh;
                    verdict.Reason = "The first deadline has not passed yet.";
                    return verdict;
                }

                tick = previous;
            }

            dueBy = tick.Value;
        }

        verdict.DueBy = dueBy;

        if (lastSuccess is null)
        {
            verdict.Status = PipelineFreshnessStatus.Never;
            verdict.Reason = "This step has never completed successfully.";
            return verdict;
        }

        verdict.LagMinutes = (long)(utcNow - lastSuccess.Value).TotalMinutes;

        if (lastSuccess >= dueBy)
        {
            verdict.Status = PipelineFreshnessStatus.Fresh;
            verdict.Reason = $"Last succeeded {Ago(verdict.LagMinutes.Value)}.";
            return verdict;
        }

        verdict.Status = PipelineFreshnessStatus.Stale;
        verdict.Reason = policy.MaxLagMinutes is { } max
            ? $"Last succeeded {Ago(verdict.LagMinutes.Value)}, which is past the {Ago(max)} limit."
            : $"Last succeeded {Ago(verdict.LagMinutes.Value)}, but was due by {dueBy:yyyy-MM-dd HH:mm} UTC.";

        return verdict;
    }

    private static string Summarize(PipelineFreshnessReport report)
    {
        var roots = report.Nodes.Where(n => n.IsRootCause).ToList();

        if (roots.Count == 0)
        {
            var covered = report.Nodes.Count(n => n.Status != PipelineFreshnessStatus.Unchecked);
            if (covered == 0) return "This pipeline has no freshness policy.";

            var unknown = report.Nodes.Count(n => n.Status == PipelineFreshnessStatus.Unknown);
            return unknown > 0
                ? $"{covered} step(s) checked; {unknown} could not be evaluated."
                : $"All {covered} checked step(s) are fresh.";
        }

        var first = roots[0];
        var name = first.Label ?? first.NodeId;
        var consequences = report.Nodes.Count(n => PipelineFreshnessStatus.IsViolation(n.Status)) - roots.Count;

        var summary = roots.Count == 1
            ? $"{name} is {first.Status.ToLowerInvariant()}. {first.Reason}"
            : $"{roots.Count} steps are late independently, starting with {name}. {first.Reason}";

        return consequences > 0
            ? $"{summary} {consequences} step(s) downstream are late as a result."
            : summary;
    }

    private static string Ago(long minutes) => minutes switch
    {
        < 1 => "just now",
        < 60 => $"{minutes} min ago",
        < 60 * 48 => $"{minutes / 60} hr ago",
        _ => $"{minutes / (60 * 24)} days ago"
    };

    // ------------------------------------------------------------------ graph shape

    /// <summary>
    /// Nodes in dependency order.
    /// <para>
    /// Deliberately not <see cref="PipelineCompiler"/>'s order, even though it has one. Compiling returns
    /// no plan for an invalid graph — and a graph that stopped validating is exactly when a freshness check
    /// matters most, because it is also the graph that stopped running. A cycle falls back to declaration
    /// order: the compiler rejects cycles, so a stored one is already broken, and ordering it perfectly is
    /// not worth failing the whole report over.
    /// </para>
    /// </summary>
    private static List<string> TopologicalOrder(PipelineGraph graph)
    {
        var ids = graph.Nodes.Select(n => n.Id).ToList();
        var known = new HashSet<string>(ids, StringComparer.Ordinal);

        var indegree = ids.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var outgoing = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            if (!known.Contains(edge.From) || !known.Contains(edge.To)) continue;
            outgoing[edge.From].Add(edge.To);
            indegree[edge.To]++;
        }

        var queue = new Queue<string>(ids.Where(id => indegree[id] == 0));
        var order = new List<string>(ids.Count);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(id);

            foreach (var next in outgoing[id])
                if (--indegree[next] == 0) queue.Enqueue(next);
        }

        // Anything left sits on a cycle. Appended in declaration order so it is still reported.
        var placed = new HashSet<string>(order, StringComparer.Ordinal);
        order.AddRange(ids.Where(id => !placed.Contains(id)));

        return order;
    }

    /// <summary>Every transitive predecessor of each node, for separating a cause from its consequences.</summary>
    private static Dictionary<string, HashSet<string>> AncestorMap(PipelineGraph graph)
    {
        var parents = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            if (!parents.TryGetValue(edge.To, out var list))
                parents[edge.To] = list = new();

            list.Add(edge.From);
        }

        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
            map[node.Id] = Walk(node.Id, parents, new HashSet<string>(StringComparer.Ordinal));

        return map;

        static HashSet<string> Walk(string id, Dictionary<string, List<string>> parents, HashSet<string> seen)
        {
            if (!parents.TryGetValue(id, out var direct)) return seen;

            foreach (var parent in direct)
                if (seen.Add(parent)) Walk(parent, parents, seen);

            return seen;
        }
    }
}
