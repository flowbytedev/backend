using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Walks a compiled pipeline in topological order and executes each step.
/// <para>
/// There is no executor-per-node-type registry here, and that is a consequence of D1 rather than a
/// shortcut: because every transform compiles to SQL, eight of the twelve node types share one code path —
/// build a SELECT, materialize it as a relation. Only sources and the destination do anything else. A
/// registry of eight classes that all called the same two lines would be ceremony.
/// </para>
/// <para>
/// One rule governs the whole class: <b>DuckDB work is serial</b>. A DuckDB file takes one writer, and the
/// engine holds no connection between steps. The only concurrency is the network-bound source fetch, which
/// touches no DuckDB and no DbContext.
/// </para>
/// </summary>
public interface IPipelineEngine
{
    /// <summary>Executes a queued run to completion. Never throws; the outcome is recorded on the run row.</summary>
    Task<PipelineRunOutcome> RunAsync(string runId, IJobProgress? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Runs the real engine over an unsaved graph, sampling sources and skipping destinations, to answer
    /// "what would this step produce?". This is what makes the mapping grid usable, and it doubles as the
    /// only test harness the feature has.
    /// </summary>
    Task<PipelinePreviewResult> PreviewAsync(PipelinePreviewRequest request, CancellationToken ct = default);
}

public partial class PipelineEngine(
    ApplicationDbContext db,
    IPipelineStore store,
    IDuckdbService duckdb,
    IPipelineSourceLoader sources,
    PipelineOptions options,
    DuckdbOption duckdbOption,
    // Optional: the scheduler and the web app both register it, but a host that only needs local datasets
    // can leave it out and get a clear refusal rather than a resolution failure at startup.
    IExternalTableWriter? externalWriter = null,
    IPipelineApiWriter? apiWriter = null,
    IPipelineExportWriter? exportWriter = null,
    IPipelineEmailSender? emailSender = null,
    // Used only to write in-flight row counts, on its own short-lived context. It cannot share the
    // engine's DbContext: the count arrives on a Progress<T> callback while the engine is blocked awaiting
    // the fetch, and two threads on one DbContext is a crash, not a race you get away with.
    IServiceScopeFactory? scopes = null) : IPipelineEngine
{
    /// <summary>Prefix for hidden per-run scratch datasets. Also what the sweeper looks for.</summary>
    public const string ScratchNamePrefix = "_pipeline_run_";

    // ---------------------------------------------------------------------- run

    public async Task<PipelineRunOutcome> RunAsync(
        string runId, IJobProgress? progress = null, CancellationToken ct = default)
    {
        var run = await db.PipelineRun.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
            return PipelineRunOutcome.Failed("That run no longer exists.", PipelineErrorType.Invalid);

        if (PipelineRunStatus.IsTerminal(run.Status))
            return new PipelineRunOutcome(run.Status == PipelineRunStatus.Success, run.Error, run.ErrorType, 0, 0);

        var log = new RunLog(progress);
        var stopwatch = Stopwatch.StartNew();

        // Claim the run. A single guarded UPDATE, so two runners racing for the same queued row cannot both
        // win — the loser sees 0 rows affected and leaves it alone.
        if (run.Status == PipelineRunStatus.Queued)
        {
            var claimed = await db.PipelineRun
                .Where(r => r.Id == runId && r.Status == PipelineRunStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, PipelineRunStatus.Running)
                    .SetProperty(r => r.RunnerId, RunnerId)
                    .SetProperty(r => r.StartedAt, DateTime.UtcNow)
                    .SetProperty(r => r.HeartbeatAt, DateTime.UtcNow), ct);

            if (claimed == 0)
                return PipelineRunOutcome.Failed("Another runner has already claimed this run.",
                    PipelineErrorType.Invalid);

            // Re-read so the tracked entity matches what is now in the database.
            db.Entry(run).State = EntityState.Detached;
            run = await db.PipelineRun.FirstAsync(r => r.Id == runId, ct);
        }

        var graph = PipelineGraph.TryParse(run.GraphJson);
        if (graph is null)
        {
            return await FinalizeAsync(run, log, stopwatch, PipelineRunStatus.Failed,
                "This run has no readable pipeline definition.", PipelineErrorType.Invalid, null, ct);
        }

        // Re-validated at run time, not just at save: graph_json is a text column, and a graph saved before
        // a catalogue change can be invalid without anyone having touched it.
        var compiled = PipelineCompiler.Compile(graph, options.ResolveMaxNodes(),
            scheduled: run.TriggerType != PipelineTriggerType.Manual);

        if (!compiled.Valid)
        {
            var first = compiled.Errors.FirstOrDefault();
            return await FinalizeAsync(run, log, stopwatch, PipelineRunStatus.Failed,
                first?.Message ?? "This pipeline is not valid.", PipelineErrorType.Invalid, first?.NodeId, ct);
        }

        var plan = compiled.Graph!;

        // A partial run: only the selected nodes and the ancestors they cannot run without. Resolved here,
        // before StepsTotal is written, so progress counts the steps that will actually run rather than
        // every node in the graph — otherwise a 5-step partial run of a 25-node pipeline reports 5/25 and
        // reads like it stalled.
        var selection = ParseSelection(run.SelectedNodesJson);

        var unknown = plan.UnknownIds(selection);
        if (unknown.Count > 0)
        {
            // Refused rather than narrowed. A selection naming a node that no longer exists means the graph
            // changed under the operator, and running the remainder would execute something other than what
            // was asked for.
            return await FinalizeAsync(run, log, stopwatch, PipelineRunStatus.Failed,
                $"This run selected step(s) that are no longer in the pipeline: {string.Join(", ", unknown)}.",
                PipelineErrorType.Invalid, unknown[0], ct);
        }

        var scope = plan.ClosureFor(selection);

        if (selection is { Count: > 0 })
        {
            var added = scope.Count - selection.Count;
            log.WriteLine(
                $"Pipeline '{run.PipelineName}' — partial run: {selection.Count} step(s) selected, "
                + $"{scope.Count} will run"
                + (added > 0 ? $" ({added} pulled in as required input)." : "."));

            foreach (var id in scope.Where(id => !selection.Contains(id)))
                log.WriteLine($"      + {plan.Node(id).Label ?? id} (required by the selection)");
        }
        else
        {
            log.WriteLine($"Pipeline '{run.PipelineName}' — {scope.Count} steps.");
        }

        run.StepsTotal = scope.Count;
        await db.SaveChangesAsync(ct);

        // The scratch dataset. A real (hidden) Dataset row, so every existing DuckDB primitive resolves its
        // path the normal way instead of this feature inventing a second path convention.
        Dataset? scratch = null;
        var status = PipelineRunStatus.Success;
        string? error = null, errorType = null, errorNode = null;

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = graph.Settings.TimeoutSeconds;
        if (timeout > 0) runCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        ExecutionContext? context = null;

        try
        {
            scratch = await CreateScratchAsync(run, runCts.Token);
            run.ScratchDatasetId = scratch.Id;
            await db.SaveChangesAsync(runCts.Token);

            // Hoisted out of the call so it survives into FinalizeAsync, which is where captured
            // watermarks are committed once the run's outcome is known.
            context = new ExecutionContext
            {
                Plan = plan,
                Graph = graph,
                CompanyId = run.CompanyId,
                ScratchDatasetId = scratch.Id!,
                Tokens = PipelineTokens.RunValues(run.Id, run.PipelineId, run.PipelineName, run.StartedAt),
                Params = ParseParams(run.ParamsJson),
                RowLimit = null,
                SkipDestinations = false,
                Scope = scope,
                Log = log,
                Run = run
            };

            await LoadWatermarksAsync(context, run.PipelineId ?? string.Empty, runCts.Token);

            var execution = await ExecuteGraphAsync(context, runCts.Token);

            status = execution.HasFailure ? PipelineRunStatus.Failed : PipelineRunStatus.Success;
            error = execution.Error;
            errorType = execution.ErrorType;
            errorNode = execution.ErrorNodeId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = PipelineRunStatus.Canceled;
            error = "The run was cancelled.";
            errorType = PipelineErrorType.Canceled;
        }
        catch (OperationCanceledException)
        {
            status = PipelineRunStatus.Failed;
            error = $"The run exceeded its {timeout}s time limit.";
            errorType = PipelineErrorType.Timeout;
        }
        catch (Exception ex)
        {
            status = PipelineRunStatus.Failed;
            error = ex.Message;
            errorType = PipelineErrorType.Unknown;
        }
        finally
        {
            // A successful run's scratch database has served its purpose. A failed one is kept, because the
            // intermediate tables are the best evidence of what went wrong; the sweeper removes it later.
            if (scratch is not null)
            {
                if (status == PipelineRunStatus.Success)
                {
                    await DeleteScratchAsync(scratch, log);
                }
                else
                {
                    log.WriteLine(
                        $"Working database kept for {options.RetainScratchOnFailureDays} day(s) so this " +
                        "failure can be inspected.");
                }
            }
        }

        return await FinalizeAsync(run, log, stopwatch, status, error, errorType, errorNode, ct, context);
    }

    // ------------------------------------------------------------------ preview

    public async Task<PipelinePreviewResult> PreviewAsync(
        PipelinePreviewRequest request, CancellationToken ct = default)
    {
        var compiled = PipelineCompiler.Compile(request.Graph, options.ResolveMaxNodes());

        // A preview deliberately tolerates an incomplete graph — you preview a step precisely because the
        // rest is not finished yet. Only errors on the path being previewed can stop it, and those surface
        // as the step's own failure below.
        var plan = compiled.Graph;
        if (plan is null)
        {
            var blocking = compiled.Errors.FirstOrDefault();
            return PipelinePreviewResult.Failed(
                blocking?.Message ?? "This pipeline is not valid yet.",
                PipelineErrorType.Invalid, blocking?.NodeId);
        }

        var rowLimit = request.RowLimit is > 0
            ? Math.Min(request.RowLimit.Value, options.ResolvePreviewRows())
            : options.ResolvePreviewRows();

        var log = new RunLog(null);
        Dataset? scratch = null;

        try
        {
            scratch = await CreateScratchAsync(
                companyId: request.CompanyId,
                name: ScratchNamePrefix + "preview_" + Guid.NewGuid().ToString("N")[..12],
                ct);

            var execution = await ExecuteGraphAsync(new ExecutionContext
            {
                Plan = plan,
                Graph = request.Graph,
                CompanyId = request.CompanyId,
                ScratchDatasetId = scratch.Id!,
                Tokens = PipelineTokens.RunValues("preview", "preview", "Preview", DateTime.UtcNow),
                Params = request.Params ?? new(),
                RowLimit = rowLimit,
                SkipDestinations = true,
                StopAfterNodeId = request.StopAfterNodeId,
                UploadedFilePath = request.UploadedFilePath,
                Log = log,
                Run = null
            }, ct);

            // Which step's output to show: the one asked for, else the last that produced anything.
            var target = request.StopAfterNodeId
                         ?? execution.Results.LastOrDefault(r => r.Value.Success).Key;

            if (target is null || !execution.Results.TryGetValue(target, out var targetResult))
            {
                return PipelinePreviewResult.Failed(
                    execution.Error ?? "Nothing ran — connect a source first.",
                    execution.ErrorType ?? PipelineErrorType.Invalid, execution.ErrorNodeId);
            }

            if (!targetResult.Success)
            {
                return new PipelinePreviewResult(false, targetResult.Error, targetResult.ErrorType,
                    target, new(), new(), execution.Schemas, execution.Traces);
            }

            var preview = await store.PreviewRelationAsync(scratch.Id!, target, rowLimit, ct);

            return new PipelinePreviewResult(true, null, null, target,
                preview.Columns, preview.Rows, execution.Schemas, execution.Traces);
        }
        catch (Exception ex)
        {
            return PipelinePreviewResult.Failed(ex.Message, PipelineErrorType.Unknown, null);
        }
        finally
        {
            // A preview's scratch database is always disposable, success or failure.
            if (scratch is not null) await DeleteScratchAsync(scratch, log);
        }
    }

    // ------------------------------------------------------------------ the walk

    private async Task<ExecutionOutcome> ExecuteGraphAsync(ExecutionContext ctx, CancellationToken ct)
    {
        var outcome = new ExecutionOutcome();
        var stepIndex = 0;

        // The nodes this execution will walk. For a full run that is every node; for a partial run it is
        // the selection's closure, already in topological order.
        var scope = ctx.Scope ?? ctx.Plan.Order;
        var inScope = new HashSet<string>(scope, StringComparer.Ordinal);

        foreach (var nodeId in scope)
        {
            ct.ThrowIfCancellationRequested();

            var node = ctx.Plan.Node(nodeId);
            var spec = ctx.Plan.Spec(nodeId);
            var label = node.Label ?? nodeId;

            // Cooperative cancellation, re-read from the database so the Cancel button works on a run this
            // process is in the middle of.
            if (ctx.Run is not null && await IsCanceledAsync(ctx.Run.Id, ct))
                throw new OperationCanceledException();

            if (ctx.SkipDestinations && spec.IsTerminal)
            {
                ctx.Log.WriteLine($"[{++stepIndex}/{scope.Count}] {label} — skipped (preview)");
                continue;
            }

            // Anything downstream of a failure cannot run: its input relation does not exist. Running it
            // with empty data would be worse than skipping, because it would look like it worked.
            var blockedBy = ctx.Plan.Predecessors[nodeId]
                .Select(l => l.Other)
                .FirstOrDefault(p => !outcome.Results.TryGetValue(p, out var r) || !r.Success);

            if (blockedBy is not null)
            {
                ctx.Log.WriteLine($"[{++stepIndex}/{scope.Count}] {label} — skipped ('{blockedBy}' did not produce data)");
                outcome.Results[nodeId] = NodeOutcome.Skipped();
                await RecordStepAsync(ctx, nodeId, node, stepIndex, PipelineStepStatus.Skipped, null, null, 0, ct);
                outcome.Skipped++;
                continue;
            }

            stepIndex++;
            ctx.Log.WriteLine($"[{stepIndex}/{scope.Count}] {label}");

            // Marked running BEFORE the work, so the run view can show this step in progress and the live
            // row counter has a row to write into. Without it a long fetch is indistinguishable from a
            // stalled run.
            await BeginStepAsync(ctx, nodeId, node, stepIndex, ct);

            var stepWatch = Stopwatch.StartNew();
            var result = await ExecuteNodeWithRetryAsync(ctx, nodeId, node, spec, outcome, ct);
            stepWatch.Stop();

            outcome.Results[nodeId] = result;
            if (result.Columns.Count > 0) outcome.Schemas[nodeId] = result.Columns;

            outcome.Traces.Add(new PipelineStepTrace(nodeId, node.Type, label,
                result.Success ? PipelineStepStatus.Success : PipelineStepStatus.Failed,
                result.RowsOut, result.Error, result.Sql, (int)stepWatch.ElapsedMilliseconds));

            await RecordStepAsync(ctx, nodeId, node, stepIndex,
                result.Success ? PipelineStepStatus.Success : PipelineStepStatus.Failed,
                result, result.Sql, (int)stepWatch.ElapsedMilliseconds, ct);

            if (result.Success)
            {
                ctx.Log.WriteLine($"      {result.RowsOut:N0} rows in {stepWatch.ElapsedMilliseconds:N0}ms");
                outcome.Completed++;

                if (ctx.Run is not null)
                {
                    if (spec.IsSource) ctx.Run.RowsRead += result.RowsOut;
                    // Accumulated separately: a run that skipped rows still reports Success, and
                    // this is the only number that says so.
                    ctx.Run.RowsRejected += result.RowsRejected;
                    if (spec.IsTerminal) ctx.Run.RowsWritten += result.RowsOut;
                }
            }
            else
            {
                ctx.Log.WriteLine($"      FAILED: {result.Error}");
                outcome.Failed++;

                // First failure wins the run's reported cause; later steps are skipped, not failures.
                outcome.Error ??= $"{label}: {result.Error}";
                outcome.ErrorType ??= result.ErrorType;
                outcome.ErrorNodeId ??= nodeId;

                var mode = node.OnError ?? ctx.Graph.Settings.OnError;
                if (mode != PipelineErrorMode.Continue) break;
            }

            if (ctx.Run is not null)
            {
                ctx.Run.StepsCompleted = outcome.Completed;
                ctx.Run.StepsFailed = outcome.Failed;
                ctx.Run.StepsSkipped = outcome.Skipped;
                ctx.Run.HeartbeatAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            if (ctx.StopAfterNodeId == nodeId) break;
        }

        // Anything the break skipped still deserves a row, so the waterfall does not simply stop. Nodes
        // outside the scope get no row at all — they were never part of this run, and a Skipped row for
        // each would drown the three steps the operator actually asked about.
        foreach (var nodeId in ctx.Plan.Order.Where(id => inScope.Contains(id) && !outcome.Results.ContainsKey(id)))
        {
            if (ctx.SkipDestinations && ctx.Plan.Spec(nodeId).IsTerminal) continue;
            if (ctx.StopAfterNodeId is not null) continue;

            outcome.Results[nodeId] = NodeOutcome.Skipped();
            outcome.Skipped++;
            await RecordStepAsync(ctx, nodeId, ctx.Plan.Node(nodeId), ++stepIndex,
                PipelineStepStatus.Skipped, null, null, 0, ct);
        }

        if (ctx.Run is not null)
        {
            ctx.Run.StepsCompleted = outcome.Completed;
            ctx.Run.StepsFailed = outcome.Failed;
            ctx.Run.StepsSkipped = outcome.Skipped;
            await db.SaveChangesAsync(ct);
        }

        return outcome;
    }

    private async Task<NodeOutcome> ExecuteNodeWithRetryAsync(
        ExecutionContext ctx, string nodeId, PipelineNodeDef node, PipelineNodeSpec spec,
        ExecutionOutcome outcome, CancellationToken ct)
    {
        var attempts = Math.Clamp(node.Retry?.MaxAttempts ?? 1, 1, 10);
        var backoff = Math.Max(0, node.Retry?.BackoffMs ?? 500);

        NodeOutcome result = NodeOutcome.Failed("Not run.", PipelineErrorType.Unknown);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            result = await ExecuteNodeAsync(ctx, nodeId, node, spec, outcome, ct);
            if (result.Success) return result;

            // Only a transient cause is worth another attempt. Retrying a bad column name or invalid SQL
            // just produces the same failure three times and buries the real message in the log.
            var transient = result.ErrorType is PipelineErrorType.DatasetBusy
                or PipelineErrorType.SourceUnavailable;

            if (!transient || attempt == attempts) return result;

            ctx.Log.WriteLine($"      attempt {attempt} failed ({result.ErrorType}); retrying");
            await Task.Delay(backoff << (attempt - 1), ct);
        }

        return result;
    }

    private async Task<NodeOutcome> ExecuteNodeAsync(
        ExecutionContext ctx, string nodeId, PipelineNodeDef node, PipelineNodeSpec spec,
        ExecutionOutcome outcome, CancellationToken ct)
    {
        // Tokens are substituted per step rather than once up front, so a failure names the step whose
        // config contained the bad token.
        string Resolve(string? text)
        {
            try { return PipelineTokens.Resolve(text, Lookup(ctx)); }
            catch (UnresolvedTokenException) { throw; }
        }

        if (spec.IsSource)
        {
            var loaded = await sources.LoadAsync(new SourceLoadRequest
            {
                Node = node,
                CompanyId = ctx.CompanyId,
                ScratchDatasetId = ctx.ScratchDatasetId,
                Relation = nodeId,
                ResolveTokens = Resolve,
                RowLimit = ctx.RowLimit,
                UploadedFilePath = ctx.UploadedFilePath,
                Progress = ctx.Log,
                RowsFetched = LiveRowCounter(ctx, nodeId),
                IncrementalLow = ctx.WatermarkLows.GetValueOrDefault(nodeId),
                OnWindowCaptured = window => ctx.CapturedWindows[nodeId] = window
            }, ct);

            if (loaded.Success && ctx.CapturedWindows.ContainsKey(nodeId))
                ctx.WatermarkRows[nodeId] = loaded.RowCount;

            if (loaded.Success && loaded.RowCount == 0 && ctx.Graph.Settings.FailOnEmptySource)
            {
                return NodeOutcome.Failed(
                    "This source returned no rows, and this pipeline is set to treat an empty source as a failure.",
                    PipelineErrorType.EmptySource, loaded.Sql);
            }

            return NodeOutcome.From(loaded);
        }

        if (spec.IsTerminal)
        {
            return node.Type switch
            {
                PipelineNodeTypes.DestinationApi => await ExecuteApiDestinationAsync(ctx, node, Resolve, ct),
                PipelineNodeTypes.DestinationEmail => await ExecuteEmailDestinationAsync(ctx, node, Resolve, ct),
                _ => await ExecuteDestinationAsync(ctx, node, Resolve, ct)
            };
        }

        // Substitution for the transforms. This used to be missing entirely: PipelineSql.Build got the raw
        // node, so a {{ }} in a filter reached DuckDB as those literal characters while the compiler
        // cheerfully validated it. Resolving into a copy fixes every transform at once and keeps
        // PipelineSql token-unaware. Each field is escaped for where it lands — see PipelineTokenResolver.
        var resolvedNode = PipelineTokenResolver.Resolve(node, spec, Lookup(ctx));

        if (node.Type == PipelineNodeTypes.TransformCapture)
            return await ExecuteCaptureAsync(ctx, resolvedNode, nodeId, ct);

        // The one step that writes several relations rather than one.
        if (node.Type == PipelineNodeTypes.TransformSwitch)
            return await ExecuteSwitchAsync(ctx, resolvedNode, nodeId, spec, outcome, ct);

        // Every remaining type is a transform, and they all take the same path: compile to SQL, materialize.
        var inputs = BuildInputs(ctx, nodeId, spec, outcome);
        var built = PipelineSql.Build(resolvedNode, inputs);

        if (!built.Success)
            return NodeOutcome.Failed(built.Error!, built.ErrorType ?? PipelineErrorType.Invalid);

        var materialized = await store.MaterializeAsync(
            ctx.ScratchDatasetId, nodeId, built.Sql!, node.TimeoutSeconds, ct);

        return NodeOutcome.From(materialized);
    }

    private async Task<NodeOutcome> ExecuteDestinationAsync(
        ExecutionContext ctx, PipelineNodeDef node, Func<string?, string> resolve, CancellationToken ct)
    {
        var config = node.Config;
        var reference = resolve(Str(config, "dataset"));
        var table = resolve(Str(config, "table"));

        if (string.IsNullOrWhiteSpace(reference))
            return NodeOutcome.Failed("This step has no destination dataset.", PipelineErrorType.Invalid);
        if (string.IsNullOrWhiteSpace(table))
            return NodeOutcome.Failed("This step has no destination table.", PipelineErrorType.Invalid);

        var dataset = await db.Dataset.AsNoTracking().FirstOrDefaultAsync(
            d => d.CompanyId == ctx.CompanyId && (d.Id == reference || d.Name == reference), ct);

        if (dataset is null)
            return NodeOutcome.Failed(
                $"No dataset called '{reference}' is available to this company.", PipelineErrorType.Invalid);

        var upstream = ctx.Plan.InputOn(node.Id, PipelinePorts.In);
        if (upstream is null)
            return NodeOutcome.Failed("This step has no input.", PipelineErrorType.Invalid);

        // "product.item" splits into schema + table. The destination step has no schema field of its own,
        // so a dotted name is the only way to say it here; a bare name keeps the engine's default schema,
        // exactly as before.
        var tableRef = PipelineTableRef.Parse(table, Str(config, "schema"));
        if (tableRef.Error is not null)
            return NodeOutcome.Failed(tableRef.Error, PipelineErrorType.Invalid);

        var mode = ParseMode(Str(config, "mode"));
        var keys = StringList(config, "keys");
        var createIfMissing = Bool(config, "createIfMissing");

        // Named as "dataset.table" only for a LOCAL dataset, where the dataset IS the database. For an
        // external one the dataset name is a FlowByte label and the real database comes from the entity's
        // connection — writing "Sales Dataset.item" would name a database that does not exist. The writer
        // logs the true target once it has resolved the connection.
        ctx.Log.WriteLine(dataset.SourceType == DatasetSourceType.External
            ? $"      writing to external dataset '{dataset.Name}' -> {tableRef.Display()} "
              + $"({mode.ToString().ToLowerInvariant()})"
            : $"      writing into {dataset.Name}.{tableRef.Table} ({mode.ToString().ToLowerInvariant()})");

        ImportResult written;

        if (dataset.SourceType == DatasetSourceType.External)
        {
            // An external dataset is a live view over a database connection, so the write leaves DuckDB
            // entirely. It needs a purpose-scoped write credential, which the writer refuses to do without.
            if (string.IsNullOrWhiteSpace(dataset.SourceEntityId))
            {
                return NodeOutcome.Failed(
                    $"'{dataset.Name}' is an external dataset but has no database connection configured.",
                    PipelineErrorType.NotWritable);
            }

            if (externalWriter is null)
            {
                return NodeOutcome.Failed(
                    "Writing into an external database is not configured on this server.",
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
                Mode = mode,
                KeyColumns = keys,
                CreateIfMissing = createIfMissing,
                BatchSize = options.ExternalWriteBatchSize,
                Progress = ctx.Log
            }, ct);
        }
        else
        {
            if (tableRef.IsQualified)
            {
                // Dropping the schema quietly would write to a different table than the one named.
                return NodeOutcome.Failed(
                    $"'{tableRef.Display()}' names a schema, but '{dataset.Name}' is a local dataset and has "
                    + "no schemas. Use just the table name.",
                    PipelineErrorType.Invalid);
            }

            written = await store.WriteRelationToTableAsync(
                ctx.ScratchDatasetId, upstream, dataset.Id!, tableRef.Table, mode, keys, createIfMissing, ct);
        }

        if (!written.Success)
            return NodeOutcome.Failed(written.Error ?? "The write failed.",
                written.ErrorType ?? PipelineErrorType.SqlError);

        var total = written.RowsInserted + written.RowsUpdated;
        ctx.Log.WriteLine(
            $"      inserted {written.RowsInserted:N0}, updated {written.RowsUpdated:N0}" +
            (written.RowsSkipped > 0 ? $", skipped {written.RowsSkipped:N0}" : string.Empty));

        return NodeOutcome.Written(total);
    }

    /// <summary>
    /// Sends the incoming relation to an HTTP endpoint.
    /// <para>
    /// Kept separate from <see cref="ExecuteDestinationAsync"/> rather than folded in behind a flag, because
    /// almost nothing is shared: there is no dataset, no table, no create-if-missing, and crucially no
    /// append/replace/upsert. Those three words promise atomicity that HTTP cannot deliver, so this step
    /// does not offer them at all instead of offering them and quietly meaning something weaker.
    /// </para>
    /// </summary>
    private async Task<NodeOutcome> ExecuteApiDestinationAsync(
        ExecutionContext ctx, PipelineNodeDef node, Func<string?, string> resolve, CancellationToken ct)
    {
        var config = node.Config;

        var credential = resolve(Str(config, "credential"));
        if (string.IsNullOrWhiteSpace(credential))
            return NodeOutcome.Failed("This step has no API credential.", PipelineErrorType.Invalid);

        var url = resolve(Str(config, "url"));
        if (string.IsNullOrWhiteSpace(url))
            return NodeOutcome.Failed("This step has no URL.", PipelineErrorType.Invalid);

        var upstream = ctx.Plan.InputOn(node.Id, PipelinePorts.In);
        if (upstream is null)
            return NodeOutcome.Failed("This step has no input.", PipelineErrorType.Invalid);

        if (apiWriter is null)
            return NodeOutcome.Failed(
                "Sending to an API is not configured on this server.", PipelineErrorType.NotWritable);

        var shape = Str(config, "shape") ?? PipelineApiWriteShapes.Batch;
        var batchSize = Int(config, "batchSize") ?? options.ResolveApiWriteBatchSize();

        var contentType = PipelineApiContentTypes.ResolveWritable(Str(config, "contentType"));

        // Form encoding silently becomes one-per-row, so the log has to say so — otherwise a step
        // configured for 500-row batches reports batches and sends singles.
        var effectiveShape = PipelineApiContentTypes.SupportsBatch(contentType)
            ? shape
            : PipelineApiWriteShapes.Row;

        ctx.Log.WriteLine(
            $"      sending to {credential}:{url} as {contentType} ("
            + (effectiveShape == PipelineApiWriteShapes.Row
                ? "one request per row"
                : $"{batchSize:N0} rows per request")
            + ")");

        var result = await apiWriter.WriteAsync(new ApiWriteRequest
        {
            CredentialReference = credential!,
            CompanyId = ctx.CompanyId,
            SourceDatasetId = ctx.ScratchDatasetId,
            SourceRelation = upstream,
            Url = url!,
            Method = Str(config, "method") ?? "POST",
            Headers = HeaderDictionary(config, "headers", resolve),
            Shape = shape,
            BatchSize = batchSize,
            ContentType = PipelineApiContentTypes.ResolveWritable(Str(config, "contentType")),
            BodyProperty = resolve(Str(config, "bodyProperty")),

            // Not `resolve`: this field is the one place in the step where a substituted value lands inside
            // JSON the author wrote, so a quote in a captured value would end the string early and hand the
            // rest of the template to the data. Everything else here is a header, a URL or a property name.
            Envelope = PipelineTokenResolver.Substitute(
                Str(config, "envelope"), PipelineTokenContexts.Json, Lookup(ctx)),
            StopOnError = config?["stopOnError"] is not JsonValue v || !v.TryGetValue<bool>(out var stop) || stop,
            Progress = ctx.Log
        }, ct);

        if (!result.Success)
            return NodeOutcome.Failed(result.Error ?? "The API write failed.",
                result.ErrorType ?? PipelineErrorType.ApiError);

        ctx.Log.WriteLine($"      sent {result.RowsSent:N0} row(s) in {result.RequestsSent:N0} request(s)");

        return NodeOutcome.Written(result.RowsSent);
    }

    private static Dictionary<string, string>? HeaderDictionary(
        JsonObject? config, string key, Func<string?, string> resolve)
    {
        if (config?[key] is not JsonObject obj || obj.Count == 0) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in obj)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            headers[name] = resolve(value?.ToString()) ?? string.Empty;
        }

        return headers.Count == 0 ? null : headers;
    }

    /// <summary>
    /// Runs a conditional split: one relation per output port.
    /// <para>
    /// The only step that breaks the engine's usual one-node-one-relation shape. The step's recorded row
    /// count is the total across outputs, which by construction equals the input count — every row goes
    /// somewhere, so if those two numbers ever diverge the routing has a hole in it.
    /// </para>
    /// </summary>
    private async Task<NodeOutcome> ExecuteSwitchAsync(
        ExecutionContext ctx, PipelineNodeDef node, string nodeId, PipelineNodeSpec spec,
        ExecutionOutcome outcome, CancellationToken ct)
    {
        var inputs = BuildInputs(ctx, nodeId, spec, outcome);
        var built = PipelineSwitch.Build(node, inputs);

        if (!built.Success)
            return NodeOutcome.Failed(built.Error!, built.ErrorType ?? PipelineErrorType.Invalid);

        long total = 0;
        var sql = new List<string>();
        List<PipelineColumn> columns = new();

        foreach (var output in built.Outputs)
        {
            var relation = PipelineRelations.For(nodeId, output.Port);

            var materialized = await store.MaterializeAsync(
                ctx.ScratchDatasetId, relation, output.Sql, node.TimeoutSeconds, ct);

            if (!materialized.Success)
                return NodeOutcome.Failed(
                    $"Routing to '{output.Port}' failed: {materialized.Error}",
                    materialized.ErrorType ?? PipelineErrorType.SqlError, output.Sql);

            total += materialized.RowCount;
            if (columns.Count == 0) columns = materialized.Columns;

            sql.Add($"-- {output.Port}" + Environment.NewLine + output.Sql);
            ctx.Log.WriteLine($"      {output.Port}: {materialized.RowCount:N0} row(s)");
        }

        // Every branch's SQL in one block. A router's whole behaviour IS its set of conditions, so showing
        // one of them on the step row would be the least useful half.
        return new NodeOutcome(true, null, null, total, columns,
            string.Join(Environment.NewLine + Environment.NewLine, sql));
    }

    /// <summary>
    /// Maps each input port to the relation feeding it. A step that accepts several inputs on one port gets
    /// them as <c>in</c>, <c>in2</c>, <c>in3</c>… in connection order, which is how the union builder finds
    /// them and keeps the generated SQL's order stable across runs.
    /// </summary>
    private static Dictionary<string, RelationInput> BuildInputs(
        ExecutionContext ctx, string nodeId, PipelineNodeSpec spec, ExecutionOutcome outcome)
    {
        var inputs = new Dictionary<string, RelationInput>(StringComparer.Ordinal);

        foreach (var port in spec.InPorts)
        {
            var feeding = ctx.Plan.LinksInto(nodeId, port);

            for (var i = 0; i < feeding.Count; i++)
            {
                var link = feeding[i];
                var columns = outcome.Results.TryGetValue(link.Other, out var r) ? r.Columns : new();
                var key = i == 0 ? port : $"{port}{i + 1}";

                // The link's FROM port decides the relation: an ordinary step writes one relation named
                // after itself, a switch writes one per output. For everything but a switch this resolves
                // to the plain node id, exactly as before.
                inputs[key] = new RelationInput
                {
                    Relation = PipelineRelations.For(link.Other, link.FromPort),
                    Columns = columns
                };
            }
        }

        return inputs;
    }

    // ------------------------------------------------------------------- scratch

    private Task<Dataset> CreateScratchAsync(PipelineRun run, CancellationToken ct) =>
        CreateScratchAsync(run.CompanyId, ScratchNamePrefix + run.Id.Replace("-", string.Empty)[..16], ct);

    private async Task<Dataset> CreateScratchAsync(string companyId, string name, CancellationToken ct)
    {
        var dataset = new Dataset
        {
            Id = Guid.NewGuid().ToString(),
            CompanyId = companyId,
            Name = name,
            Description = "Working database for a pipeline run. Created and removed automatically.",
            SourceType = DatasetSourceType.Local,
            IsUserDataset = false,
            Path = options.ResolveScratchDirectory(duckdbOption.DuckdbFilePath),
            CreatedBy = "pipeline",
            CreatedAt = DateTime.UtcNow
        };

        // Inserted directly rather than through DatasetService: that path also grants the creating user
        // admin rights on the dataset, which would put a throwaway working database in their dataset list.
        db.Dataset.Add(dataset);
        await db.SaveChangesAsync(ct);

        await duckdb.CreateDatabaseAsync(dataset);
        return dataset;
    }

    private async Task DeleteScratchAsync(Dataset scratch, RunLog log)
    {
        try
        {
            await duckdb.DeleteDatabaseAsync(scratch);
        }
        catch (Exception ex)
        {
            // The sweeper will get it. Never fail a finished run over cleanup.
            log.WriteLine($"Could not remove the working database: {ex.Message}");
        }

        try
        {
            db.Dataset.Remove(scratch);
            await db.SaveChangesAsync();
        }
        catch
        {
            // Same reasoning.
        }
    }

    // ------------------------------------------------------------------ recording

    /// <summary>
    /// Marks a step as running, before it starts.
    /// <para>
    /// Without this a step is invisible until it finishes: the run view has no row to show, so a source
    /// spending eighty seconds fetching looks identical to nothing happening. It also gives the in-flight
    /// row counter somewhere to write.
    /// </para>
    /// </summary>
    private async Task BeginStepAsync(
        ExecutionContext ctx, string nodeId, PipelineNodeDef node, int stepIndex, CancellationToken ct)
    {
        if (ctx.Run is null) return;

        var existing = await db.PipelineRunStep
            .FirstOrDefaultAsync(x => x.RunId == ctx.Run.Id && x.NodeId == nodeId, ct);

        if (existing is not null)
        {
            // A retry re-enters the same step; reset it rather than leaving the previous attempt's numbers.
            existing.Status = PipelineStepStatus.Running;
            existing.StartedAt = DateTime.UtcNow;
            existing.RowsOut = null;
            existing.Error = null;
        }
        else
        {
            db.PipelineRunStep.Add(new PipelineRunStep
            {
                RunId = ctx.Run.Id,
                CompanyId = ctx.Run.CompanyId,
                PipelineId = ctx.Run.PipelineId,
                NodeId = nodeId,
                NodeType = node.Type,
                NodeLabel = node.Label,
                StepIndex = stepIndex,
                Status = PipelineStepStatus.Running,
                StartedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A row counter that writes what a step has fetched so far, so the run view can count up live.
    /// <para>
    /// Two things make this safe. It writes on its OWN DbContext from a fresh scope, because the callback
    /// fires on a pool thread while the engine is blocked inside the fetch — sharing the engine's context
    /// would be concurrent use of one DbContext. And it is throttled by time rather than by row count: a
    /// row-count throttle either floods a fast source or never fires on a slow one.
    /// </para>
    /// <para>
    /// Failures are swallowed. This is a progress indicator; it must never be the thing that fails a load.
    /// </para>
    /// </summary>
    private IProgress<long>? LiveRowCounter(ExecutionContext ctx, string nodeId)
    {
        if (ctx.Run is null || scopes is null) return null;

        var runId = ctx.Run.Id;
        var lastWrite = DateTime.UtcNow;
        var writing = 0;

        return new Progress<long>(rows =>
        {
            if (DateTime.UtcNow - lastWrite < TimeSpan.FromSeconds(1.5)) return;

            // One write in flight at a time. Progress<T> can fire again while the previous write is still
            // going, and queuing them up would turn a fast fetch into a write storm.
            if (Interlocked.Exchange(ref writing, 1) == 1) return;

            lastWrite = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var fresh = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    await fresh.PipelineRunStep
                        .Where(x => x.RunId == runId && x.NodeId == nodeId)
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.RowsOut, rows));
                }
                catch
                {
                    // Progress only. A failed counter update must not disturb the run.
                }
                finally
                {
                    Interlocked.Exchange(ref writing, 0);
                }
            });
        });
    }

    private async Task RecordStepAsync(
        ExecutionContext ctx, string nodeId, PipelineNodeDef node, int stepIndex, string status,
        NodeOutcome? result, string? sql, int durationMs, CancellationToken ct)
    {
        if (ctx.Run is null) return;   // a preview keeps its trace in memory rather than in the database

        var previewRows = options.ResolveStepPreviewRows();
        string? previewJson = null;

        // A cheap read of what the step produced, so the run view can show it without keeping the scratch
        // database around. Only for steps that produced a relation.
        if (status == PipelineStepStatus.Success && previewRows > 0 && result is { RowsOut: > 0 }
            && !ctx.Plan.Spec(nodeId).IsTerminal)
        {
            try
            {
                var preview = await store.PreviewRelationAsync(ctx.ScratchDatasetId, nodeId, previewRows, ct);
                previewJson = JsonSerializer.Serialize(preview.Rows);
            }
            catch
            {
                // A preview is a nicety; never let it turn a successful step into a failure.
            }
        }

        // The row already exists — BeginStepAsync inserted it as Running so the run view could show the
        // step working. Update it rather than adding a second row for the same step.
        var row = await db.PipelineRunStep
            .FirstOrDefaultAsync(x => x.RunId == ctx.Run.Id && x.NodeId == nodeId, ct);

        if (row is not null)
        {
            row.Status = status;
            row.StepIndex = stepIndex;
            row.RowsOut = result?.RowsOut;
            row.RowsRejected = result is null or { RowsRejected: 0 } ? null : result.RowsRejected;
            row.SqlText = sql;
            row.OutputPreviewJson = previewJson;
            row.OutputColumnsJson = result is { Columns.Count: > 0 }
                ? JsonSerializer.Serialize(result.Columns)
                : null;
            row.Error = result?.Error;
            row.ErrorType = result?.ErrorType;
            row.DurationMs = durationMs;
            row.CompletedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            return;
        }

        db.PipelineRunStep.Add(new PipelineRunStep
        {
            RunId = ctx.Run.Id,
            CompanyId = ctx.Run.CompanyId,
            PipelineId = ctx.Run.PipelineId,
            NodeId = nodeId,
            NodeType = node.Type,
            NodeLabel = node.Label,
            StepIndex = stepIndex,
            Status = status,
            RowsOut = result?.RowsOut,
            // result is nullable here (a skipped step has none), and 0 is stored as null so the run
            // view can show a blank rather than a misleading "0 rejected" on every ordinary step.
            RowsRejected = result is null or { RowsRejected: 0 } ? null : result.RowsRejected,
            SqlText = sql,
            OutputPreviewJson = previewJson,
            OutputColumnsJson = result is { Columns.Count: > 0 }
                ? JsonSerializer.Serialize(result.Columns)
                : null,
            Error = result?.Error,
            ErrorType = result?.ErrorType,
            DurationMs = durationMs,
            StartedAt = DateTime.UtcNow.AddMilliseconds(-durationMs),
            CompletedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);
    }

    private async Task<PipelineRunOutcome> FinalizeAsync(
        PipelineRun run, RunLog log, Stopwatch stopwatch, string status,
        string? error, string? errorType, string? errorNodeId, CancellationToken ct,
        ExecutionContext? ctx = null)
    {
        stopwatch.Stop();

        log.WriteLine(status == PipelineRunStatus.Success
            ? $"Finished in {stopwatch.ElapsedMilliseconds:N0}ms — {run.RowsWritten:N0} rows written."
            : $"{status} after {stopwatch.ElapsedMilliseconds:N0}ms. {error}");

        run.Status = status;
        run.Error = error;
        run.ErrorType = errorType;
        run.ErrorNodeId = errorNodeId;
        run.DurationMs = (int)stopwatch.ElapsedMilliseconds;
        run.FinishedAt = DateTime.UtcNow;
        run.HeartbeatAt = DateTime.UtcNow;
        run.Log = log.Text;

        // Mirror onto the pipeline row so the list page needs no join. CancellationToken deliberately not
        // passed: a cancelled run still has to record that it was cancelled.
        var pipeline = await db.Pipeline.FirstOrDefaultAsync(p => p.Id == run.PipelineId, CancellationToken.None);
        if (pipeline is not null)
        {
            pipeline.LastRunAt = run.FinishedAt;
            pipeline.LastRunStatus = status;
            pipeline.LastRunMessage = error;
            pipeline.LastRunRows = run.RowsWritten;
            pipeline.RunCount += 1;
        }

        // Watermarks advance here and nowhere else: after the run is known to have succeeded, in the same
        // save as the run's terminal status. A null ctx means the run never got as far as executing a
        // graph, so there is nothing captured to commit.
        if (status == PipelineRunStatus.Success && ctx is not null)
            await CommitWatermarksAsync(ctx, run);

        await db.SaveChangesAsync(CancellationToken.None);

        return new PipelineRunOutcome(status == PipelineRunStatus.Success, error, errorType,
            run.RowsRead, run.RowsWritten);
    }

    /// <summary>
    /// Loads the committed watermark for every source node, once, before the walk starts.
    /// <para>
    /// Up front rather than per node so all sources in one run see a consistent starting point, and so a
    /// pipeline with six sources does one query rather than six.
    /// </para>
    /// </summary>
    private async Task LoadWatermarksAsync(ExecutionContext ctx, string pipelineId, CancellationToken ct)
    {
        if (ctx.IsPreview) return;

        var rows = await db.PipelineState.AsNoTracking()
            .Where(x => x.CompanyId == ctx.CompanyId && x.PipelineId == pipelineId)
            .Select(x => new { x.NodeId, x.WatermarkValue })
            .ToListAsync(ct);

        foreach (var row in rows)
            ctx.WatermarkLows[row.NodeId] = row.WatermarkValue;
    }

    /// <summary>
    /// Advances the watermark for each source that captured a window this run.
    /// <para>
    /// A null ceiling is NOT committed. That happens when the source had no non-null value in the watermark
    /// column, and writing null would erase a real mark — turning the next run into a full reload of a table
    /// that had simply gone quiet.
    /// </para>
    /// <para>
    /// <b>Nor is a watermark committed when this run did not execute everything downstream of that source.</b>
    /// A partial run is, from the point of view of a branch it left out, indistinguishable from a failed one:
    /// advance the mark and the next run starts above rows that branch never loaded, silently and
    /// permanently. So a source's mark advances only once every descendant of it has actually consumed the
    /// window — which for a full run is always true, and for a partial run is exactly the condition worth
    /// checking.
    /// </para>
    /// <para>
    /// Holding the mark is not free either: re-reading a window means an <c>append</c> destination that DID
    /// run can load those rows twice. That is the better failure — a duplicate is visible in the data,
    /// whereas a gap is visible nowhere — and the log says which sources were held so the choice is not a
    /// surprise.
    /// </para>
    /// </summary>
    private async Task CommitWatermarksAsync(ExecutionContext ctx, PipelineRun run)
    {
        if (ctx.IsPreview || ctx.CapturedWindows.Count == 0) return;

        var existing = await db.PipelineState
            .Where(x => x.CompanyId == ctx.CompanyId && x.PipelineId == run.PipelineId)
            .ToListAsync(CancellationToken.None);

        foreach (var (nodeId, window) in ctx.CapturedWindows)
        {
            if (window.High is null)
            {
                ctx.Log.WriteLine(
                    $"  {nodeId}: watermark left at {window.Low ?? "(unset)"} — the source had nothing to read.");
                continue;
            }

            // The scope check. Only meaningful on a partial run; on a full run every descendant is in scope
            // by definition, so this never holds a mark that used to advance.
            if (ctx.Scope is not null)
            {
                var inScope = new HashSet<string>(ctx.Scope, StringComparer.Ordinal);
                var unconsumed = ctx.Plan.Descendants(nodeId).Where(d => !inScope.Contains(d)).ToList();

                if (unconsumed.Count > 0)
                {
                    ctx.Log.WriteLine(
                        $"  {nodeId}: watermark HELD at {window.Low ?? "(unset)"} — this run did not include "
                        + $"{unconsumed.Count} step(s) downstream of it, so advancing would skip rows they "
                        + "have never read. The next full run will re-read this window.");
                    continue;
                }
            }

            var row = existing.FirstOrDefault(x => x.NodeId == nodeId);

            if (row is null)
            {
                row = new PipelineState
                {
                    CompanyId = ctx.CompanyId,
                    PipelineId = run.PipelineId ?? string.Empty,
                    NodeId = nodeId,
                    CreatedOn = DateTime.Now
                };
                db.PipelineState.Add(row);
            }

            row.WatermarkValue = window.High;
            row.WatermarkType = window.Type;
            row.RowsLastRun = ctx.WatermarkRows.GetValueOrDefault(nodeId);
            row.AdvancedAt = DateTime.UtcNow;
            row.AdvancedByRunId = run.Id;
            row.ModifiedOn = DateTime.Now;

            ctx.Log.WriteLine($"  {nodeId}: watermark advanced to {window.High}");
        }
    }

    private async Task<bool> IsCanceledAsync(string runId, CancellationToken ct)
    {
        var status = await db.PipelineRun.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(ct);

        return status == PipelineRunStatus.Canceled;
    }

    // -------------------------------------------------------------------- helpers

    private static string RunnerId => $"{Environment.MachineName}:{Environment.ProcessId}";

    private static Func<string, string?> Lookup(ExecutionContext ctx) => path =>
    {
        if (ctx.Tokens.TryGetValue(path, out var value)) return value;

        // Captured values. Checked before params so a graph cannot shadow one with a request parameter —
        // params arrive from an API caller, and letting them override a value the pipeline computed would
        // hand an external caller control of a filter.
        if (ctx.Vars.TryGetValue(path, out var captured)) return captured;

        if (path.StartsWith(PipelineTokens.ParamsRoot + ".", StringComparison.OrdinalIgnoreCase))
        {
            var key = path[(PipelineTokens.ParamsRoot.Length + 1)..];
            return ctx.Params.TryGetValue(key, out var supplied) ? supplied : null;
        }

        return null;
    };

    private static Dictionary<string, string> ParseParams(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return result;
            foreach (var (key, value) in obj)
                result[key] = value?.ToString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // A malformed params blob leaves the tokens unresolved, which fails the step that uses one
            // with a message naming it — better than failing the whole run with a JSON parse error.
        }

        return result;
    }

    private static ImportMode ParseMode(string? mode) => (mode ?? string.Empty).ToLowerInvariant() switch
    {
        PipelineWriteModes.Replace => ImportMode.Replace,
        PipelineWriteModes.Upsert => ImportMode.Upsert,
        _ => ImportMode.Append
    };

    /// <summary>
    /// The stored selection for a partial run. A malformed or empty array is treated as "no selection", so
    /// the run does the whole pipeline rather than nothing — an empty scope would succeed having done
    /// nothing at all, which is the one outcome nobody wants from pressing Run.
    /// </summary>
    private static List<string>? ParseSelection(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json)
                ?.Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return ids is { Count: > 0 } ? ids : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? Str(JsonObject? config, string key)
    {
        var value = config?[key];
        return value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    /// <summary>
    /// A numeric config value. Tolerates a string, because the inspector's number control and hand-written
    /// YAML do not always agree on whether 500 is a number or "500".
    /// </summary>
    private static int? Int(JsonObject? config, string key)
    {
        var value = config?[key];
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        return int.TryParse(v.ToString(), out var parsed) ? parsed : null;
    }

    private static bool Bool(JsonObject? config, string key)
    {
        var value = config?[key];
        return value is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    private static List<string> StringList(JsonObject? config, string key)
    {
        var result = new List<string>();
        if (config?[key] is not JsonArray array) return result;

        foreach (var item in array)
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                result.Add(s);

        return result;
    }

    // -------------------------------------------------------------- inner types

    private sealed class ExecutionContext
    {
        public required CompiledPipelineGraph Plan { get; init; }
        public required PipelineGraph Graph { get; init; }
        public required string CompanyId { get; init; }
        public required string ScratchDatasetId { get; init; }
        public required Dictionary<string, string> Tokens { get; init; }
        public required Dictionary<string, string> Params { get; init; }
        public required int? RowLimit { get; init; }
        public required bool SkipDestinations { get; init; }

        /// <summary>
        /// The nodes to execute, in topological order. Null means every node — a full run.
        /// <para>
        /// Always a closed set: every node in it has all of its inputs in it too, which is what lets the
        /// existing "did my predecessor produce data?" check keep working untouched. An open set would make
        /// every partial run report its first step as blocked.
        /// </para>
        /// </summary>
        public IReadOnlyList<string>? Scope { get; init; }

        public string? StopAfterNodeId { get; init; }
        public string? UploadedFilePath { get; init; }
        public required RunLog Log { get; init; }

        /// <summary>Null during a preview, which writes nothing to the database.</summary>
        public required PipelineRun? Run { get; init; }

        /// <summary>Committed watermark per source node, read once at the start of the run.</summary>
        public Dictionary<string, string?> WatermarkLows { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Windows captured during this run, held until the run succeeds.
        /// <para>
        /// Held rather than written as they are captured, because a watermark that advances for a run which
        /// then fails skips its window permanently — the next run starts above rows that were never loaded.
        /// Nothing here is persisted unless the whole run reaches Success.
        /// </para>
        /// </summary>
        public Dictionary<string, PipelineWatermarkWindow> CapturedWindows { get; } = new(StringComparer.Ordinal);

        /// <summary>Rows each incremental source read, recorded for the state row's diagnostics.</summary>
        public Dictionary<string, long> WatermarkRows { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Values published by <c>transform.capture</c> steps, keyed by full token path (<c>vars.x</c>).
        /// <para>
        /// Mutable during the walk, which is what makes this work at all: tokens are substituted per step as
        /// that step executes, so a value captured at step 3 is available to step 4. The compiler guarantees
        /// the capture comes first by turning the reference itself into an ordering dependency.
        /// </para>
        /// </summary>
        public Dictionary<string, string> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A preview must never move a watermark, however it ends.</summary>
        public bool IsPreview => Run is null || RowLimit is not null;
    }

    private sealed class ExecutionOutcome
    {
        public Dictionary<string, NodeOutcome> Results { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<PipelineColumn>> Schemas { get; } = new(StringComparer.Ordinal);
        public List<PipelineStepTrace> Traces { get; } = new();

        public int Completed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }


        public string? Error { get; set; }
        public string? ErrorType { get; set; }
        public string? ErrorNodeId { get; set; }

        /// <summary>
        /// True when any step failed. A failure fails the run even under onError=continue: continue only
        /// controls whether the remaining steps get a chance to run, not whether the run was successful.
        /// </summary>
        public bool HasFailure => Failed > 0;
    }

    private sealed record NodeOutcome(
        bool Success, string? Error, string? ErrorType, long RowsOut,
        List<PipelineColumn> Columns, string? Sql, long RowsRejected = 0)
    {
        public static NodeOutcome From(PipelineRelationResult r) =>
            new(r.Success, r.Error, r.ErrorType, r.RowCount, r.Columns, r.Sql, r.RowsRejected);

        public static NodeOutcome Written(long rows) => new(true, null, null, rows, new(), null);

        public static NodeOutcome Failed(string error, string errorType, string? sql = null) =>
            new(false, error, errorType, 0, new(), sql);

        public static NodeOutcome Skipped() =>
            new(false, null, null, 0, new(), null);
    }

    /// <summary>
    /// Progress sink that both forwards to Hangfire's console and accumulates the text, so a run's log can
    /// be read in the app without opening the Hangfire dashboard. Mirrors what IngestionService does.
    /// </summary>
    private sealed class RunLog(IJobProgress? inner) : IJobProgress
    {
        private readonly StringBuilder _text = new();

        public string Text => _text.ToString();

        public void WriteLine(string message)
        {
            _text.AppendLine(message);
            inner?.WriteLine(message);
        }

        public void SetProgress(int percent) => inner?.SetProgress(percent);
    }
}

/// <summary>What a finished run amounted to.</summary>
public sealed record PipelineRunOutcome(
    bool Success, string? Error, string? ErrorType, long RowsRead, long RowsWritten)
{
    public static PipelineRunOutcome Failed(string error, string errorType) =>
        new(false, error, errorType, 0, 0);
}

/// <summary>One step as a preview reports it — enough to light up the canvas without a database round trip.</summary>
public sealed record PipelineStepTrace(
    string NodeId, string NodeType, string Label, string Status,
    long RowsOut, string? Error, string? Sql, int DurationMs);

public sealed class PipelinePreviewRequest
{
    public required PipelineGraph Graph { get; init; }
    public required string CompanyId { get; init; }

    /// <summary>Which step's output to return. Null runs everything except destinations.</summary>
    public string? StopAfterNodeId { get; init; }

    public int? RowLimit { get; init; }
    public Dictionary<string, string>? Params { get; init; }
    public string? UploadedFilePath { get; init; }
}

public sealed record PipelinePreviewResult(
    bool Success,
    string? Error,
    string? ErrorType,
    string? NodeId,
    List<PipelineColumn> Columns,
    List<Dictionary<string, object?>> Rows,
    /// <summary>Every step's resulting columns, so the editor can refresh its whole schema cache at once.</summary>
    Dictionary<string, List<PipelineColumn>> Schemas,
    List<PipelineStepTrace> Steps)
{
    public static PipelinePreviewResult Failed(string error, string errorType, string? nodeId) =>
        new(false, error, errorType, nodeId, new(), new(), new(), new());
}
