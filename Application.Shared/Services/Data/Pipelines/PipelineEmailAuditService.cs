using System.Text.Json.Nodes;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Answers, for a saved pipeline, "can every recipient of an email step actually receive what it sends?".
/// <para>
/// The question exists because <c>destination.email</c> has two very different delivery modes and only one
/// of them works for a stranger. An attachment reaches anybody with a mailbox. A dataset link — the fallback
/// when the export is too large — reaches only somebody who can sign in and has been granted the table. So a
/// recipient who is not a FlowByte user is perfectly fine on one setting and silently broken on the other,
/// and nothing in the graph makes that visible.
/// </para>
/// <para>
/// Read-only. It reports; it never grants. Granting is a separate, explicit act — see the
/// <c>sharing/grant-table</c> endpoint — because widening who can read data should never be a side effect of
/// pressing Save.
/// </para>
/// </summary>
public interface IPipelineEmailAuditService
{
    Task<PipelineEmailAudit> AuditAsync(string companyId, string pipelineId, CancellationToken ct = default);
}

public class PipelineEmailAuditService(
    ApplicationDbContext db,
    IDatasetSharingService sharing) : IPipelineEmailAuditService
{
    public async Task<PipelineEmailAudit> AuditAsync(
        string companyId, string pipelineId, CancellationToken ct = default)
    {
        var audit = new PipelineEmailAudit();

        var pipeline = await db.Pipeline.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == pipelineId, ct);

        if (pipeline is null) return audit;

        var graph = PipelineGraph.TryParse(pipeline.GraphJson);
        if (graph is null) return audit;

        var emailNodes = graph.Nodes
            .Where(n => n.Type == PipelineNodeTypes.DestinationEmail)
            .ToList();

        if (emailNodes.Count == 0) return audit;

        foreach (var node in emailNodes)
        {
            var step = await AuditNodeAsync(companyId, node, ct);
            if (step is not null) audit.Steps.Add(step);
        }

        return audit;
    }

    private async Task<PipelineEmailStepAudit?> AuditNodeAsync(
        string companyId, PipelineNodeDef node, CancellationToken ct)
    {
        var config = node.Config;

        var recipients = Recipients(config);
        if (recipients.Count == 0) return null;

        var step = new PipelineEmailStepAudit
        {
            NodeId = node.Id,
            Label = node.Label ?? node.Id,
            SendsDatasetLink = Str(config, "onOversize") == PipelineEmailOversizeBehaviour.DatasetLink
        };

        // A token is resolved when the run starts, so at save time there is nothing to look up. Reported
        // rather than dropped: "we could not check these" is information, and pretending a token is a
        // valid recipient would be a false clean bill of health.
        var literal = new List<string>();

        foreach (var address in recipients)
        {
            if (address.Contains("{{", StringComparison.Ordinal)) step.Unresolved.Add(address);
            else literal.Add(address);
        }

        if (literal.Count == 0) return step;

        var users = await sharing.ResolveUsersByEmailAsync(literal, ct);

        // The linked table, when this step falls back to a dataset link. Resolved once for the step.
        Dataset? linkDataset = null;
        string? linkTable = null;

        if (step.SendsDatasetLink)
        {
            var reference = Str(config, "linkDataset");
            var table = Str(config, "linkTable");

            if (!string.IsNullOrWhiteSpace(reference))
            {
                linkDataset = await db.Dataset.AsNoTracking().FirstOrDefaultAsync(
                    d => d.CompanyId == companyId && (d.Id == reference || d.Name == reference), ct);
            }

            if (!string.IsNullOrWhiteSpace(table))
            {
                var parsed = PipelineTableRef.Parse(table, null);
                if (parsed.Error is null) linkTable = parsed.Table;
            }

            step.LinkDatasetId = linkDataset?.Id;
            step.LinkDatasetName = linkDataset?.Name;
            step.LinkTable = linkTable;

            // A local dataset is the only kind whose grants this app enforces. An external one is a live
            // view over somebody else's database, and a share row there would promise access this app does
            // not control — so say so rather than offer a grant that means less than it looks like.
            step.LinkDatasetIsExternal = linkDataset?.SourceType == DatasetSourceType.External;
        }

        foreach (var address in literal)
        {
            if (!users.TryGetValue(address, out var user))
            {
                // Not a user. Which list it belongs in depends entirely on what this step sends.
                if (step.SendsDatasetLink) step.Blockers.Add(address);
                else step.NonUsers.Add(address);
                continue;
            }

            // A user, on a step that only ever attaches a file: nothing to grant, nothing to warn about.
            if (!step.SendsDatasetLink || linkDataset?.Id is null || string.IsNullOrWhiteSpace(linkTable))
            {
                step.Users.Add(Recipient(user));
                continue;
            }

            if (step.LinkDatasetIsExternal)
            {
                step.Users.Add(Recipient(user));
                continue;
            }

            var access = await sharing.GetUserDatasetAccessAsync(linkDataset.Id!, user.UserId);

            // Tables empty means the whole dataset, so that already covers this table.
            var covered = access.HasAccess
                          && (access.Tables.Count == 0
                              || access.Tables.Contains(linkTable!, StringComparer.OrdinalIgnoreCase));

            if (covered) step.AlreadyShared.Add(Recipient(user));
            else step.Grantable.Add(Recipient(user));
        }

        return step;
    }

    private static PipelineEmailRecipient Recipient(ResolvedShareUser user) => new()
    {
        Email = user.Email,
        UserId = user.UserId,
        DisplayName = user.DisplayName
    };

    /// <summary>
    /// The step's recipients, flattened the same way the sender flattens them — one row may hold several
    /// addresses separated by a comma, semicolon or newline. Checking the raw rows instead would report
    /// "a@x.com, b@y.com" as one unknown address.
    /// </summary>
    private static List<string> Recipients(JsonObject? config)
    {
        var rows = new List<string>();

        foreach (var key in new[] { "to", "cc", "bcc" })
        {
            if (config?[key] is not JsonArray array) continue;

            foreach (var item in array)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    rows.Add(s);
            }
        }

        return rows
            .SelectMany(r => r.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? Str(JsonObject? config, string key) =>
        config?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;
}
