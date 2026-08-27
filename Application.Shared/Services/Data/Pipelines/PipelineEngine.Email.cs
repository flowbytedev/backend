using System.Globalization;
using System.Text.Json.Nodes;
using Application.Shared.Enums;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// The <c>destination.email</c> step. In its own file for the same reason
/// <c>DuckdbService.Pipelines.cs</c> is: it is the one destination that produces an artefact rather than
/// writing rows somewhere, so almost none of the main file's machinery applies to it.
/// </summary>
public partial class PipelineEngine
{
    /// <summary>
    /// Exports the incoming relation to a file and emails it.
    /// <para>
    /// The hard part here is not sending, it is the size ceiling. An attachment has an upper bound this side
    /// does not control, and a pipeline's row count is not knowable when the graph is authored — so the same
    /// configuration can work for months and then exceed it on the day the data grows. That is why "too
    /// large" is a configured behaviour and not a validation rule: the fallback writes the rows into a
    /// dataset using the very same call <see cref="ExecuteDestinationAsync"/> makes, and emails a link, so
    /// the recipient still gets the data — under that dataset's permissions rather than as an attachment,
    /// which has none.
    /// </para>
    /// </summary>
    private async Task<NodeOutcome> ExecuteEmailDestinationAsync(
        ExecutionContext ctx, PipelineNodeDef node, Func<string?, string> resolve, CancellationToken ct)
    {
        var config = node.Config;

        if (emailSender is null || exportWriter is null || !emailSender.IsConfigured)
        {
            return NodeOutcome.Failed(
                "Sending email is not configured on this server. Set PipelineEmail:ApiBaseUri and "
                + "PipelineEmail:From in appsettings.",
                PipelineErrorType.NotWritable);
        }

        var recipients = PipelineEmailSender.Clean(StringList(config, "to").Select(resolve));
        if (recipients.Count == 0)
            return NodeOutcome.Failed("This step has no valid recipient address.", PipelineErrorType.Invalid);

        var subject = resolve(Str(config, "subject"));
        if (string.IsNullOrWhiteSpace(subject))
            return NodeOutcome.Failed("This step has no subject.", PipelineErrorType.Invalid);

        var upstream = ctx.Plan.InputOn(node.Id, PipelinePorts.In);
        if (upstream is null)
            return NodeOutcome.Failed("This step has no input.", PipelineErrorType.Invalid);

        var format = Str(config, "format") ?? PipelineExportFormats.Csv;
        var pipelineName = ctx.Tokens.GetValueOrDefault("run.pipelineName") is { Length: > 0 } name
            ? name
            : "pipeline";

        // Counted before anything is exported: two of the three empty behaviours mean no file is built at
        // all, and building one only to throw it away is an expensive way to learn that.
        var counted = await store.ReadScalarAsync(
            ctx.ScratchDatasetId, $"SELECT count(*) FROM {QuoteRelation(upstream)}", ct: ct);

        if (!counted.Success)
            return NodeOutcome.Failed(
                counted.Error ?? "The row count could not be read.", PipelineErrorType.SqlError);

        var rows = Convert.ToInt64(counted.Value ?? 0L, CultureInfo.InvariantCulture);

        if (rows == 0)
        {
            switch (Str(config, "onEmpty") ?? PipelineEmailEmptyBehaviour.Send)
            {
                case PipelineEmailEmptyBehaviour.Skip:
                    ctx.Log.WriteLine("      no rows — nothing sent, as this step is configured");
                    return NodeOutcome.Written(0);

                case PipelineEmailEmptyBehaviour.Fail:
                    return NodeOutcome.Failed(
                        "This export has no rows, and this step is set to treat that as a failure.",
                        PipelineErrorType.EmptySource);
            }
        }

        // Row cap first: it is knowable without building anything, and a workbook over Excel's own limit
        // cannot be opened at all, so there is no point assembling one.
        if (rows > emailSender.MaxRows)
        {
            var spilled = await SendDatasetLinkAsync(
                ctx, node, resolve, recipients, subject!, pipelineName, rows, upstream,
                $"{rows:N0} rows is over the {emailSender.MaxRows:N0} this server will export", ct);

            return spilled ?? NodeOutcome.Failed(
                $"This export is {rows:N0} rows, over the {emailSender.MaxRows:N0} limit for an emailed "
                + "file. Filter the rows down, or set this step to write to a dataset and send a link.",
                PipelineErrorType.Invalid);
        }

        // One directory per node per run, so two email steps in one graph cannot collide on a file name.
        var exportDir = Path.Combine(
            options.ResolveScratchDirectory(duckdbOption.DuckdbFilePath),
            "_exports", $"{ctx.Run?.Id ?? "preview"}_{DirectorySafe(node.Id)}");

        try
        {
            var baseName = resolve(Str(config, "fileName"));
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = $"{pipelineName}_{ctx.Tokens.GetValueOrDefault("run.date")}";

            var compress = Bool(config, "compress");

            ctx.Log.WriteLine(
                $"      exporting {rows:N0} row(s) as {PipelineExportFormats.Extension(format)}"
                + (compress ? " (zipped)" : string.Empty));

            var file = await exportWriter.WriteAsync(new ExportFileRequest
            {
                SourceDatasetId = ctx.ScratchDatasetId,
                SourceRelation = upstream,
                TargetDirectory = exportDir,
                BaseName = baseName!,
                Format = format,
                Delimiter = Str(config, "delimiter") ?? ",",
                IncludeHeader = TrueByDefault(config, "includeHeader"),
                SheetName = resolve(Str(config, "sheetName")) is { Length: > 0 } sheet ? sheet : "Data",
                Compress = compress,
                MaxRows = emailSender.MaxRows,
                Progress = ctx.Log
            }, ct);

            if (!file.Success)
                return NodeOutcome.Failed(
                    file.Error ?? "The export failed.", file.ErrorType ?? PipelineErrorType.SqlError);

            // The size is only knowable now, once the file exists — which is precisely why oversize has to
            // be a run-time policy rather than something the compiler could have caught.
            if (file.Bytes > emailSender.MaxAttachmentBytes)
            {
                var reason = $"the file is {PipelineEmailSender.Mb(file.Bytes)}, over the "
                             + $"{PipelineEmailSender.Mb(emailSender.MaxAttachmentBytes)} attachment limit";

                var spilled = await SendDatasetLinkAsync(
                    ctx, node, resolve, recipients, subject!, pipelineName, rows, upstream, reason, ct);

                return spilled ?? NodeOutcome.Failed(
                    $"The export is {PipelineEmailSender.Mb(file.Bytes)}, over the "
                    + $"{PipelineEmailSender.Mb(emailSender.MaxAttachmentBytes)} attachment limit. "
                    + (compress ? string.Empty : "Turn on \"Zip the file\", ")
                    + "filter the rows down, or set this step to write to a dataset and send a link.",
                    PipelineErrorType.Invalid);
            }

            ctx.Log.WriteLine(
                $"      emailing {Path.GetFileName(file.Path!)} ({PipelineEmailSender.Mb(file.Bytes)}) "
                + $"to {recipients.Count} recipient(s)");

            var sent = await emailSender.SendAsync(new EmailSendRequest
            {
                To = recipients,
                Cc = StringList(config, "cc").Select(resolve).ToList(),
                Bcc = StringList(config, "bcc").Select(resolve).ToList(),
                ReplyTo = StringList(config, "replyTo").Select(resolve).ToList(),
                Subject = subject!,
                Message = resolve(Str(config, "body")),
                PipelineName = pipelineName,
                RunId = ctx.Run?.Id,
                Rows = rows,
                AttachmentPath = file.Path,
                AttachmentName = Path.GetFileName(file.Path!)
            }, ct);

            if (!sent.Success)
                return NodeOutcome.Failed(
                    sent.Error ?? "The email could not be sent.",
                    sent.ErrorType ?? PipelineErrorType.ApiError);

            ctx.Log.WriteLine($"      sent to {string.Join(", ", recipients)}");
            return NodeOutcome.Written(rows);
        }
        finally
        {
            // The attachment is delivered; the file on disk is not a deliverable. Left behind it grows
            // without bound, and it is a copy of the data sitting outside the datasets that govern it.
            TryDeleteDirectory(exportDir);
        }
    }

    /// <summary>
    /// The "too large to attach" path: write the rows to a dataset table and email a link to it. Returns
    /// null when this step is set to fail instead, so the caller reports the size in its own words.
    /// <para>
    /// This reuses the dataset write rather than serving the file from a download endpoint, and that is the
    /// security-relevant decision in this feature. A link that works from an inbox works for anyone holding
    /// the email; a link into the app is still gated by the recipient's own dataset grants, including the
    /// column masking and row-level security that apply there.
    /// </para>
    /// </summary>
    private async Task<NodeOutcome?> SendDatasetLinkAsync(
        ExecutionContext ctx, PipelineNodeDef node, Func<string?, string> resolve,
        IReadOnlyList<string> recipients, string subject, string pipelineName, long rows,
        string upstream, string reason, CancellationToken ct)
    {
        var config = node.Config;

        if ((Str(config, "onOversize") ?? PipelineEmailOversizeBehaviour.Fail)
            != PipelineEmailOversizeBehaviour.DatasetLink)
        {
            return null;
        }

        var reference = resolve(Str(config, "linkDataset"));
        var table = resolve(Str(config, "linkTable"));

        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(table))
        {
            return NodeOutcome.Failed(
                $"This export is too large to attach ({reason}), and this step has no dataset to write it "
                + "to instead.",
                PipelineErrorType.Invalid);
        }

        var dataset = await db.Dataset.AsNoTracking().FirstOrDefaultAsync(
            d => d.CompanyId == ctx.CompanyId && (d.Id == reference || d.Name == reference), ct);

        if (dataset is null)
            return NodeOutcome.Failed(
                $"No dataset called '{reference}' is available to this company.", PipelineErrorType.Invalid);

        var tableRef = PipelineTableRef.Parse(table, null);
        if (tableRef.Error is not null)
            return NodeOutcome.Failed(tableRef.Error, PipelineErrorType.Invalid);

        ctx.Log.WriteLine($"      too large to attach — {reason}");
        ctx.Log.WriteLine($"      writing into {dataset.Name}.{tableRef.Table} and sending a link instead");

        ImportResult written;

        if (dataset.SourceType == DatasetSourceType.External)
        {
            if (externalWriter is null || string.IsNullOrWhiteSpace(dataset.SourceEntityId))
            {
                return NodeOutcome.Failed(
                    $"'{dataset.Name}' is an external dataset and cannot be written to on this server.",
                    PipelineErrorType.NotWritable);
            }

            written = await externalWriter.WriteAsync(new ExternalWriteRequest
            {
                EntityId = dataset.SourceEntityId!,
                CompanyId = ctx.CompanyId,
                SourceDatasetId = ctx.ScratchDatasetId,
                SourceRelation = upstream,
                Schema = tableRef.Schema,
                Table = tableRef.Table,
                Mode = ImportMode.Replace,
                KeyColumns = [],
                CreateIfMissing = true,
                BatchSize = options.ExternalWriteBatchSize,
                Progress = ctx.Log
            }, ct);
        }
        else
        {
            if (tableRef.IsQualified)
            {
                return NodeOutcome.Failed(
                    $"'{tableRef.Display()}' names a schema, but '{dataset.Name}' is a local dataset and "
                    + "has no schemas. Use just the table name.",
                    PipelineErrorType.Invalid);
            }

            // Replace, never append: this table holds one run's export. Last run's rows still being in it
            // would make the linked table show more than the email says it does.
            written = await store.WriteRelationToTableAsync(
                ctx.ScratchDatasetId, upstream, dataset.Id!, tableRef.Table,
                ImportMode.Replace, [], createIfMissing: true, ct);
        }

        if (!written.Success)
            return NodeOutcome.Failed(
                written.Error ?? "The dataset write failed.",
                written.ErrorType ?? PipelineErrorType.SqlError);

        var appBase = (emailSender!.AppBaseUri ?? string.Empty).TrimEnd('/');
        var url = string.IsNullOrEmpty(appBase)
            ? null
            : $"{appBase}/data/tables?c={Uri.EscapeDataString(ctx.CompanyId)}"
              + $"&d={Uri.EscapeDataString(dataset.Id ?? string.Empty)}";

        var sent = await emailSender.SendAsync(new EmailSendRequest
        {
            To = recipients,
            Cc = StringList(config, "cc").Select(resolve).ToList(),
            Bcc = StringList(config, "bcc").Select(resolve).ToList(),
            ReplyTo = StringList(config, "replyTo").Select(resolve).ToList(),
            Subject = subject,
            Message = resolve(Str(config, "body")),
            PipelineName = pipelineName,
            RunId = ctx.Run?.Id,
            Rows = rows,
            LinkUrl = url,
            LinkLabel = $"{dataset.Name} — {tableRef.Display()}",
            LinkReason = $"This export was too large to attach ({reason}), so the rows were written to a "
                         + "dataset table instead. Opening it uses your usual sign-in."
        }, ct);

        if (!sent.Success)
            return NodeOutcome.Failed(
                sent.Error ?? "The email could not be sent.",
                sent.ErrorType ?? PipelineErrorType.ApiError);

        ctx.Log.WriteLine($"      sent a dataset link to {string.Join(", ", recipients)}");
        return NodeOutcome.Written(written.RowsInserted + written.RowsUpdated);
    }

    /// <summary>
    /// A checkbox that is on unless it is explicitly off. Absent must mean true here: a CSV silently losing
    /// its header row because a field was never touched is not a sensible default.
    /// </summary>
    private static bool TrueByDefault(JsonObject? config, string key) =>
        config?[key] is not JsonValue value || !value.TryGetValue<bool>(out var flag) || flag;

    private static string QuoteRelation(string relation) =>
        '"' + relation.Replace("\"", "\"\"") + '"';

    /// <summary>A node id reduced to what a directory name may safely contain.</summary>
    private static string DirectorySafe(string value)
    {
        var kept = value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return kept.Length == 0 ? "node" : new string(kept);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* a leftover export is swept with the run's scratch; never fail a delivered email over it */ }
    }
}
