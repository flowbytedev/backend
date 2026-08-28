using System.Text.Json.Nodes;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Turns a stored graph document into something executable, or into a list of problems the editor can point
/// at. Lives in <c>Application.Shared</c> so all three places that need the same answer get it from the
/// same code: the browser lints while you type, the web app validates on save, and the engine re-validates
/// at run time. That last one is not redundant — <c>graph_json</c> is a text column, and a graph saved
/// before a catalogue change can be invalid without anyone having touched it.
/// </summary>
public static class PipelineCompiler
{
    /// <summary>Refuses graphs beyond this size; also the engine's guard against a pathological document.</summary>
    public const int DefaultMaxNodes = 60;

    public static PipelineCompileResult Compile(
        string? graphJson, int maxNodes = DefaultMaxNodes, bool scheduled = false)
    {
        var graph = PipelineGraph.TryParse(graphJson);
        if (graph is null)
        {
            return PipelineCompileResult.Failed([
                new(null, PipelineIssueCodes.GraphUnreadable,
                    "The pipeline is missing or is not valid JSON.")
            ]);
        }
        return Compile(graph, maxNodes, scheduled);
    }

    /// <param name="scheduled">
    /// True when this graph is about to be attached to a cron or API trigger. It changes one rule: a
    /// file source set to "uploaded when the pipeline is run" is fine for a manual run and impossible for
    /// an unattended one, so it is only an error in the scheduled case. Rejecting it at save time is the
    /// whole point — the alternative is a pipeline that looks healthy and fails every night at 3am.
    /// </param>
    public static PipelineCompileResult Compile(
        PipelineGraph graph, int maxNodes = DefaultMaxNodes, bool scheduled = false)
    {
        var issues = new List<PipelineValidationIssue>();

        if (graph.SchemaVersion > PipelineGraph.CurrentSchemaVersion)
        {
            return PipelineCompileResult.Failed([
                new(null, PipelineIssueCodes.GraphSchemaVersion,
                    $"This pipeline was authored by a newer version of FlowByte (schema {graph.SchemaVersion}; " +
                    $"this build understands {PipelineGraph.CurrentSchemaVersion}).")
            ]);
        }

        if (graph.Nodes.Count == 0)
        {
            return PipelineCompileResult.Failed([
                new(null, PipelineIssueCodes.GraphEmpty, "Add a source to get started.")
            ]);
        }

        if (graph.Nodes.Count > maxNodes)
        {
            issues.Add(new(null, PipelineIssueCodes.GraphTooLarge,
                $"This pipeline has {graph.Nodes.Count} steps; the limit is {maxNodes}."));
        }

        // ---- 1. Nodes: unique, well-formed ids; known types; required config ----
        var nodes = new Dictionary<string, PipelineNodeDef>(StringComparer.Ordinal);
        var specs = new Dictionary<string, PipelineNodeSpec>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                issues.Add(new(null, PipelineIssueCodes.NodeIdMissing, "A step has no id."));
                continue;
            }
            if (!IsValidNodeId(node.Id))
            {
                // The id becomes a relation name in generated SQL and a YAML `from:` reference, so it is
                // restricted rather than escaped at every use site.
                issues.Add(new(node.Id, PipelineIssueCodes.NodeIdInvalid,
                    $"Step id '{node.Id}' must be 1-64 characters of letters, digits, underscore or dash."));
                continue;
            }
            if (!nodes.TryAdd(node.Id, node))
            {
                issues.Add(new(node.Id, PipelineIssueCodes.NodeIdDuplicate, $"Duplicate step id '{node.Id}'."));
                continue;
            }

            var spec = PipelineNodeCatalog.Get(node.Type);
            if (spec is null)
            {
                issues.Add(new(node.Id, PipelineIssueCodes.NodeTypeUnknown,
                    $"'{node.Type}' is not a known step type in this version of FlowByte."));
                continue;
            }
            specs[node.Id] = spec;

            ValidateConfig(node, spec, issues, scheduled);
            ValidateTokenRoots(node, issues);
        }

        if (specs.Count == 0) return PipelineCompileResult.Failed(issues);

        // ---- 2. Edges: endpoints exist, ports are real, cardinality holds ----
        var successors = specs.Keys.ToDictionary(id => id, _ => new List<PipelineLink>(), StringComparer.Ordinal);
        var predecessors = specs.Keys.ToDictionary(id => id, _ => new List<PipelineLink>(), StringComparer.Ordinal);
        var seenEdges = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            if (!specs.ContainsKey(edge.From ?? ""))
            {
                issues.Add(new(edge.From, PipelineIssueCodes.EdgeFromMissing,
                    $"A connection starts at '{edge.From}', which is not a valid step."));
                continue;
            }
            if (!specs.ContainsKey(edge.To ?? ""))
            {
                issues.Add(new(edge.From, PipelineIssueCodes.EdgeToMissing,
                    $"A connection from '{edge.From}' points at '{edge.To}', which is not a valid step."));
                continue;
            }

            var fromPort = string.IsNullOrWhiteSpace(edge.FromPort) ? PipelinePorts.Out : edge.FromPort;
            var toPort = string.IsNullOrWhiteSpace(edge.ToPort) ? PipelinePorts.In : edge.ToPort;

            // Asked of the NODE, not the spec: a switch's outputs come from its configuration, so a
            // spec lookup would reject every edge leaving one.
            var outPorts = PipelineNodeCatalog.OutPortsFor(nodes[edge.From!]);
            if (!outPorts.Contains(fromPort))
            {
                issues.Add(new(edge.From, PipelineIssueCodes.EdgeFromPortInvalid,
                    $"'{Name(nodes, edge.From!)}' has no output '{fromPort}'." +
                    (outPorts.Count == 0 ? " It is a destination, so nothing can follow it." : "")));
                continue;
            }
            if (!specs[edge.To!].InPorts.Contains(toPort))
            {
                issues.Add(new(edge.To, PipelineIssueCodes.EdgeToPortInvalid,
                    $"'{Name(nodes, edge.To!)}' has no input '{toPort}'." +
                    (specs[edge.To!].IsSource ? " It is a source, so nothing can feed into it." : "")));
                continue;
            }

            // Collapse exact duplicates rather than erroring — the editor can produce them harmlessly.
            if (!seenEdges.Add($"{edge.From}{fromPort}{edge.To}{toPort}")) continue;

            successors[edge.From!].Add(new PipelineLink(edge.To!, fromPort, toPort, edge.Id));
            predecessors[edge.To!].Add(new PipelineLink(edge.From!, fromPort, toPort, edge.Id));
        }

        // ---- 3. Shape: sources in, destinations out, every port fed exactly once ----
        var sourceIds = specs.Where(kv => kv.Value.IsSource).Select(kv => kv.Key).ToList();
        var destinationIds = specs.Where(kv => kv.Value.IsTerminal).Select(kv => kv.Key).ToList();

        if (sourceIds.Count == 0)
        {
            issues.Add(new(null, PipelineIssueCodes.GraphNoSource,
                "Add a source — a pipeline needs somewhere to read data from."));
        }
        if (destinationIds.Count == 0)
        {
            issues.Add(new(null, PipelineIssueCodes.GraphNoDestination,
                "Add a destination — without one this pipeline would read data and throw it away."));
        }

        foreach (var id in specs.Keys)
        {
            var spec = specs[id];
            var inbound = predecessors[id];

            if (spec.IsSource)
            {
                // Already reported per-edge as an invalid target port, so nothing to add here.
            }
            else if (inbound.Count == 0)
            {
                issues.Add(new(id, PipelineIssueCodes.NodeUnreachable,
                    $"'{Name(nodes, id)}' has nothing connected to it, so it will never run."));
            }
            else
            {
                foreach (var port in spec.InPorts)
                {
                    var feeding = inbound.Count(l => l.ToPort == port);

                    if (feeding == 0)
                    {
                        issues.Add(new(id, PipelineIssueCodes.NodePortNotConnected,
                            spec.InPorts.Count > 1
                                ? $"'{Name(nodes, id)}' needs something connected to its {port} input."
                                : $"'{Name(nodes, id)}' needs an input."));
                    }
                    else if (feeding > 1 && !spec.AllowsMultipleInputs)
                    {
                        // Quietly using the first of two would be a genuinely nasty bug to chase, so this
                        // is an error rather than a warning.
                        issues.Add(new(id, PipelineIssueCodes.NodeTooManyInputs,
                            $"'{Name(nodes, id)}' takes one input on {port} but has {feeding}. " +
                            "Use a Stack rows step to combine them first."));
                    }
                }
            }

            if (!spec.IsTerminal && successors[id].Count == 0)
            {
                issues.Add(new(id, PipelineIssueCodes.NodeDanglingOutput,
                    $"Nothing uses the result of '{Name(nodes, id)}', so this step does no work.",
                    PipelineIssueSeverity.Warning));
            }
        }

        // ---- 4. Kahn: topological order + layers. The residual set IS the cycle, so the error is free. ----
        var layer = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        var remainingInDegree = specs.Keys.ToDictionary(
            id => id,
            id => predecessors[id].Select(l => l.Other).Distinct(StringComparer.Ordinal).Count(),
            StringComparer.Ordinal);

        // Deterministic seeding and tie-breaking: document order, never Dictionary enumeration order.
        // Non-determinism here shows up as the canvas reshuffling itself on every save.
        var documentOrder = graph.Nodes.Where(n => specs.ContainsKey(n.Id)).Select(n => n.Id).ToList();
        var documentIndex = documentOrder
            .Select((id, i) => (id, i))
            .ToDictionary(t => t.id, t => t.i, StringComparer.Ordinal);

        var ready = new List<string>(documentOrder.Where(id => remainingInDegree[id] == 0));
        foreach (var id in ready) layer[id] = 0;

        while (ready.Count > 0)
        {
            var id = ready[0];
            ready.RemoveAt(0);
            order.Add(id);

            foreach (var link in successors[id].DistinctBy(l => l.Other, StringComparer.Ordinal))
            {
                var next = link.Other;
                layer[next] = Math.Max(layer.GetValueOrDefault(next, 0), layer[id] + 1);
                if (--remainingInDegree[next] == 0)
                {
                    var insertAt = ready.FindIndex(r => documentIndex[r] > documentIndex[next]);
                    if (insertAt < 0) ready.Add(next); else ready.Insert(insertAt, next);
                }
            }
        }

        if (order.Count != specs.Count)
        {
            var inCycle = specs.Keys.Where(id => !order.Contains(id)).OrderBy(id => documentIndex[id]).ToList();
            issues.Add(new(inCycle.FirstOrDefault(), PipelineIssueCodes.GraphCycle,
                $"These steps form a loop: {string.Join(" -> ", inCycle.Select(i => Name(nodes, i)))}. " +
                "Data has to flow one way."));
            return PipelineCompileResult.Failed(issues);
        }

        // ---- 5. Column-level checks against the cached schemas ----
        // Warnings only, deliberately. The cache can be stale in either direction, and blocking a save on
        // it would mean you cannot fix a pipeline whose source is temporarily unreachable. The executor
        // does the same check against the real relation, where it is an error.
        ValidateColumnsAgainstCache(graph, nodes, specs, predecessors, issues);

        if (issues.Any(i => i.Severity == PipelineIssueSeverity.Error))
            return PipelineCompileResult.Failed(issues);

        var waves = order
            .GroupBy(id => layer[id])
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<string>)g.ToList())
            .ToList();

        var compiled = new CompiledPipelineGraph(
            graph, nodes, specs, order, waves, layer, successors, predecessors,
            sourceIds, destinationIds);

        return new PipelineCompileResult(compiled, issues);
    }

    // ---------------- helpers ----------------

    private static string Name(Dictionary<string, PipelineNodeDef> nodes, string id) =>
        nodes.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n.Label) ? n.Label! : id;

    private static bool IsValidNodeId(string id)
    {
        if (id.Length is 0 or > 64) return false;
        foreach (var c in id)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-') return false;
        return true;
    }

    private static void ValidateConfig(
        PipelineNodeDef node, PipelineNodeSpec spec, List<PipelineValidationIssue> issues, bool scheduled)
    {
        foreach (var field in spec.Fields.Where(f => f.Required))
        {
            // A field hidden by VisibleWhen is not required — asking for a blob container on a node
            // reading from a folder would be nonsense.
            if (!IsVisible(field, node)) continue;

            var value = node.Config?[field.Key];
            var missing = value switch
            {
                null => true,
                JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s),
                JsonObject o => o.Count == 0,
                JsonArray a => a.Count == 0,
                _ => false
            };

            if (missing)
                issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldRequired,
                    $"'{node.Label ?? node.Id}' needs {field.Label}."));
        }

        if (node.OnError is not null
            && node.OnError != PipelineErrorMode.Fail
            && node.OnError != PipelineErrorMode.Continue)
        {
            issues.Add(new(node.Id, PipelineIssueCodes.NodeOnErrorInvalid,
                $"'{node.OnError}' is not a valid error mode (fail or continue)."));
        }

        if (node.Retry is { MaxAttempts: < 1 or > 10 })
            issues.Add(new(node.Id, PipelineIssueCodes.NodeRetryRange, "Retry attempts must be between 1 and 10."));

        ValidateTypeSpecifics(node, issues, scheduled);
    }

    private static void ValidateTypeSpecifics(
        PipelineNodeDef node, List<PipelineValidationIssue> issues, bool scheduled)
    {
        var config = node.Config;

        switch (node.Type)
        {
            case PipelineNodeTypes.SourceFile:
            {
                var location = Str(config, "location");

                // The rule that earns this whole parameter. An uploaded file exists for exactly one run.
                if (scheduled && location == PipelineFileLocations.Upload)
                {
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeUploadNotSchedulable,
                        $"'{node.Label ?? node.Id}' reads a file you upload at run time, so this pipeline " +
                        "cannot be scheduled or triggered by API. Point it at a folder or at blob storage instead."));
                }
                if (location is not null && !PipelineFileLocations.All.Contains(location))
                {
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldInvalid,
                        $"'{location}' is not a valid file location."));
                }
                break;
            }

            case PipelineNodeTypes.SourceDatabase:
            {
                var mode = Str(config, "mode");
                if (mode == "query" && string.IsNullOrWhiteSpace(Str(config, "query")))
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldRequired,
                        $"'{node.Label ?? node.Id}' needs a query."));
                if (mode == "table" && string.IsNullOrWhiteSpace(Str(config, "table")))
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldRequired,
                        $"'{node.Label ?? node.Id}' needs a table name."));

                // Paging needs a column to page on; a size alone does nothing and would look like it worked.
                if (Int(config, "batchSize") is > 0 && string.IsNullOrWhiteSpace(Str(config, "batchKeyColumn")))
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldInvalid,
                        $"'{node.Label ?? node.Id}' sets a batch size but no batch column, so it will still " +
                        "read everything in one go.", PipelineIssueSeverity.Warning));
                break;
            }

            case PipelineNodeTypes.TransformSql:
            {
                var sql = Str(config, "sql");
                if (!string.IsNullOrWhiteSpace(sql) && !SelectOnlyGuard.IsSafeSelect(sql, out var error))
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeSqlInvalid,
                        $"'{node.Label ?? node.Id}': {error}"));
                break;
            }

            case PipelineNodeTypes.TransformDedupe:
            {
                // "first" and "last" are meaningless without an order — DuckDB would return an arbitrary
                // row, stably enough to look correct in testing and change in production.
                var keep = Str(config, "keep");
                if (keep is "first" or "last" && string.IsNullOrWhiteSpace(Str(config, "orderBy")))
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldInvalid,
                        $"'{node.Label ?? node.Id}' keeps the {keep} row but has no column to order by, " +
                        "so which row survives is arbitrary.", PipelineIssueSeverity.Warning));
                break;
            }

            case PipelineNodeTypes.DestinationDataset:
            {
                var mode = Str(config, "mode");
                if (mode is not null && !PipelineWriteModes.All.Contains(mode))
                {
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldInvalid,
                        $"'{mode}' is not a valid write mode."));
                }
                if (mode == PipelineWriteModes.Upsert && (config?["keys"] as JsonArray)?.Count is null or 0)
                {
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldRequired,
                        $"'{node.Label ?? node.Id}' updates matching rows, so it needs the columns that " +
                        "identify a row."));
                }
                break;
            }

            case PipelineNodeTypes.DestinationApi:
            {
                var contentType = Str(config, "contentType");

                if (contentType is not null && !PipelineApiContentTypes.Writable.Contains(contentType))
                {
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldInvalid,
                        $"'{contentType}' is not a format this step can build a body in. " +
                        $"Available: {string.Join(", ", PipelineApiContentTypes.Writable)}."));
                }

                // Caught here rather than left to the run-time backstop, because the two settings LOOK
                // compatible in the form: a step reading "500 rows per request" that in fact sends one row
                // per request is a surprise best had at save time.
                if (!PipelineApiContentTypes.SupportsBatch(contentType)
                    && Str(config, "shape") == PipelineApiWriteShapes.Batch)
                {
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldInvalid,
                        $"'{node.Label ?? node.Id}' sends form-encoded bodies, which cannot hold a list, " +
                        "so it will send one request per row rather than batches.",
                        PipelineIssueSeverity.Warning));
                }

                break;
            }

            case PipelineNodeTypes.DestinationEmail:
            {
                // Recipients: caught here rather than at run time because an email step with an empty To is
                // a graph that cannot ever succeed, and finding that out from a failed 3am run is worse than
                // finding it out while editing.
                var recipients = (config?["to"] as JsonArray)?
                    .Select(v => v?.ToString())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList() ?? [];

                if (recipients.Count == 0)
                {
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldRequired,
                        $"'{node.Label ?? node.Id}' has no recipient."));
                }

                // A token resolves at run time, so only a literal can be checked. A row with neither an @
                // nor a token is a typo every time.
                foreach (var address in recipients)
                {
                    if (address!.Contains("{{", StringComparison.Ordinal)) continue;
                    if (address.Contains('@', StringComparison.Ordinal)) continue;

                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldInvalid,
                        $"'{address}' is not an email address."));
                }

                var format = Str(config, "format");
                if (format is not null && !PipelineExportFormats.All.Contains(format))
                {
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldInvalid,
                        $"'{format}' is not a file format this can attach."));
                }

                if (string.IsNullOrWhiteSpace(Str(config, "subject")))
                {
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldRequired,
                        $"'{node.Label ?? node.Id}' has no subject."));
                }

                // The oversize fallback is the one setting whose absence only bites on the day the data
                // grows past the limit — which is exactly the day nobody is watching. So it is an error at
                // save time, not a run-time surprise.
                if (Str(config, "onOversize") == PipelineEmailOversizeBehaviour.DatasetLink)
                {
                    if (string.IsNullOrWhiteSpace(Str(config, "linkDataset")))
                        issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldRequired,
                            $"'{node.Label ?? node.Id}' sends a dataset link when the export is too big, " +
                            "so it needs the dataset to write into."));

                    if (string.IsNullOrWhiteSpace(Str(config, "linkTable")))
                        issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldRequired,
                            $"'{node.Label ?? node.Id}' sends a dataset link when the export is too big, " +
                            "so it needs the table to write into."));
                }

                // A multi-character delimiter is silently truncated to its first character downstream, and
                // silent truncation of a field somebody typed deserves a word here.
                var delimiter = Str(config, "delimiter");
                if (delimiter is { Length: > 1 } && delimiter != "\\t")
                {
                    issues.Add(new(node.Id, PipelineIssueCodes.NodeFieldInvalid,
                        $"A CSV delimiter is one character; only the '{delimiter[0]}' in '{delimiter}' " +
                        "would be used.", PipelineIssueSeverity.Warning));
                }

                break;
            }
        }
    }

    /// <summary>Catches typos like <c>{{ rnu.date }}</c> at save time instead of mid-run.</summary>
    private static void ValidateTokenRoots(PipelineNodeDef node, List<PipelineValidationIssue> issues)
    {
        foreach (var path in PipelineTokens.ReferencedPaths(node.Config))
        {
            var root = path.Split('.', 2)[0];
            if (!PipelineTokens.Roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new(node.Id, PipelineIssueCodes.TokenUnknownRoot,
                    $"'{{{{ {path} }}}}' is not something a pipeline can substitute. " +
                    $"Available: {string.Join(", ", PipelineTokens.Roots.Select(r => r + ".*"))}."));
            }
        }
    }

    /// <summary>
    /// Compares each node's column references against the columns its upstream node produced last time.
    /// This is what turns "the run failed at 3am" into "you renamed CUSTNO and this step still asks for it",
    /// visible while you edit. Warnings only — see the call site for why.
    /// </summary>
    private static void ValidateColumnsAgainstCache(
        PipelineGraph graph,
        Dictionary<string, PipelineNodeDef> nodes,
        Dictionary<string, PipelineNodeSpec> specs,
        Dictionary<string, List<PipelineLink>> predecessors,
        List<PipelineValidationIssue> issues)
    {
        if (graph.Schemas is null || graph.Schemas.Count == 0) return;

        foreach (var (id, node) in nodes)
        {
            if (!specs.TryGetValue(id, out var spec) || spec.IsSource) continue;

            // The primary input's cached columns. For a join, the left side; for everything else, the
            // single input.
            var primary = predecessors[id]
                .FirstOrDefault(l => l.ToPort is PipelinePorts.In or PipelinePorts.Left);
            if (primary is null) continue;

            var upstream = graph.SchemaFor(primary.Other);
            if (upstream.Count == 0) continue;

            var available = new HashSet<string>(upstream.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var referenced in ReferencedColumns(node))
            {
                if (available.Contains(referenced)) continue;

                issues.Add(new(id, PipelineIssueCodes.ColumnMissing,
                    $"'{node.Label ?? id}' uses column '{referenced}', which '{Name(nodes, primary.Other)}' " +
                    $"no longer produces. Available: {string.Join(", ", upstream.Take(12).Select(c => c.Name))}" +
                    (upstream.Count > 12 ? ", …" : "") + ".",
                    PipelineIssueSeverity.Warning));
            }
        }
    }

    /// <summary>
    /// Column names a node names <em>structurally</em> — a mapping's source column, a dedupe key, a
    /// group-by. Free-text SQL (a filter condition, a compute expression, a SQL step) is deliberately not
    /// parsed: half-parsing SQL to guess at column references produces false positives on aliases and
    /// functions, and a wrong warning on every save is worse than no warning.
    /// </summary>
    private static IEnumerable<string> ReferencedColumns(PipelineNodeDef node)
    {
        var config = node.Config;
        if (config is null) yield break;

        switch (node.Type)
        {
            case PipelineNodeTypes.TransformMap:
                if (config["columns"] is JsonObject map)
                {
                    foreach (var kv in map)
                    {
                        // { out: { source: "in_col", cast: "INTEGER" } } — a constant names no column.
                        var source = (kv.Value as JsonObject)?["source"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(source)) yield return source!;
                    }
                }
                foreach (var name in StringArray(config, "drop")) yield return name;
                break;

            case PipelineNodeTypes.TransformDedupe:
                foreach (var name in StringArray(config, "keys")) yield return name;
                break;

            case PipelineNodeTypes.TransformAggregate:
                foreach (var name in StringArray(config, "groupBy")) yield return name;
                if (config["metrics"] is JsonArray metrics)
                {
                    foreach (var m in metrics)
                    {
                        var column = (m as JsonObject)?["column"]?.GetValue<string>();
                        // count(*) legitimately has no column.
                        if (!string.IsNullOrWhiteSpace(column) && column != "*") yield return column!;
                    }
                }
                break;

            case PipelineNodeTypes.SourceDataset:
                foreach (var name in StringArray(config, "columns")) yield return name;
                break;

            case PipelineNodeTypes.DestinationDataset:
                foreach (var name in StringArray(config, "keys")) yield return name;
                break;
        }
    }

    /// <summary>
    /// Whether a field's <c>VisibleWhen</c> is satisfied. Grammar: <c>key=a|b</c> is any-of, and <c>&amp;</c>
    /// joins conditions that must ALL hold.
    /// <para>
    /// Must behave identically to <c>PipelineInspector.IsVisible</c> — a disagreement means this requires a
    /// field the form never showed, which is an error nobody can act on.
    /// </para>
    /// </summary>
    private static bool IsVisible(PipelineFieldSpec field, PipelineNodeDef node)
    {
        if (string.IsNullOrWhiteSpace(field.VisibleWhen)) return true;

        foreach (var clause in field.VisibleWhen.Split('&', StringSplitOptions.RemoveEmptyEntries))
            if (!ClauseHolds(clause, node)) return false;

        return true;
    }

    private static bool ClauseHolds(string clause, PipelineNodeDef node)
    {
        var parts = clause.Split('=', 2);
        if (parts.Length != 2) return true;

        var actual = node.Config?[parts[0]];
        var actualText = actual switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
            _ => actual.ToString()
        };

        // "key=a|b" means any-of. Page size applies to both page and offset pagination, and duplicating
        // the field once per mode would put two controls with the same meaning in the same form.
        return parts[1]
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Any(allowed => string.Equals(actualText, allowed, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Str(JsonObject? config, string key)
    {
        var value = config?[key];
        return value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private static int? Int(JsonObject? config, string key)
    {
        var value = config?[key];
        return value is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    }

    private static IEnumerable<string> StringArray(JsonObject? config, string key)
    {
        if (config?[key] is not JsonArray array) yield break;
        foreach (var item in array)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                yield return s;
        }
    }
}

/// <summary>One end of a resolved edge, from the perspective of the node holding the list.</summary>
public sealed record PipelineLink(string Other, string FromPort, string ToPort, string? EdgeId);

public static class PipelineIssueSeverity
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>
/// Stable machine-readable issue codes. The editor maps them to highlights and quick fixes, so they are
/// constants rather than inline strings.
/// </summary>
public static class PipelineIssueCodes
{
    public const string GraphUnreadable = "graph.unreadable";
    public const string GraphSchemaVersion = "graph.schemaVersion";
    public const string GraphEmpty = "graph.empty";
    public const string GraphTooLarge = "graph.tooLarge";
    public const string GraphNoSource = "graph.noSource";
    public const string GraphNoDestination = "graph.noDestination";
    public const string GraphCycle = "graph.cycle";

    public const string NodeIdMissing = "node.idMissing";
    public const string NodeIdInvalid = "node.idInvalid";
    public const string NodeIdDuplicate = "node.idDuplicate";
    public const string NodeTypeUnknown = "node.typeUnknown";
    public const string NodeFieldRequired = "node.fieldRequired";
    public const string NodeFieldInvalid = "node.fieldInvalid";
    public const string NodeOnErrorInvalid = "node.onErrorInvalid";
    public const string NodeRetryRange = "node.retryRange";
    public const string NodeUnreachable = "node.unreachable";
    public const string NodePortNotConnected = "node.portNotConnected";
    public const string NodeTooManyInputs = "node.tooManyInputs";
    public const string NodeDanglingOutput = "node.danglingOutput";
    public const string NodeUploadNotSchedulable = "node.uploadNotSchedulable";
    public const string NodeSqlInvalid = "node.sqlInvalid";

    public const string EdgeFromMissing = "edge.fromMissing";
    public const string EdgeToMissing = "edge.toMissing";
    public const string EdgeFromPortInvalid = "edge.fromPortInvalid";
    public const string EdgeToPortInvalid = "edge.toPortInvalid";

    public const string TokenUnknownRoot = "token.unknownRoot";
    public const string ColumnMissing = "column.missing";
}

/// <summary>A validation finding. <c>NodeId</c> lets the editor highlight the offending step.</summary>
public sealed record PipelineValidationIssue(
    string? NodeId,
    string Code,
    string Message,
    string Severity = PipelineIssueSeverity.Error);

/// <summary>Compile output: either a runnable graph or the reasons it is not one. Warnings accompany success.</summary>
public sealed record PipelineCompileResult(
    CompiledPipelineGraph? Graph,
    IReadOnlyList<PipelineValidationIssue> Issues)
{
    public bool Valid => Graph is not null;

    public IEnumerable<PipelineValidationIssue> Errors =>
        Issues.Where(i => i.Severity == PipelineIssueSeverity.Error);

    public IEnumerable<PipelineValidationIssue> Warnings =>
        Issues.Where(i => i.Severity == PipelineIssueSeverity.Warning);

    public static PipelineCompileResult Failed(IReadOnlyList<PipelineValidationIssue> issues) => new(null, issues);
}

/// <summary>A validated graph plus everything the engine needs to walk it, computed once.</summary>
public sealed record CompiledPipelineGraph(
    PipelineGraph Document,
    IReadOnlyDictionary<string, PipelineNodeDef> Nodes,
    IReadOnlyDictionary<string, PipelineNodeSpec> Specs,
    /// <summary>Topological order, tie-broken by document order so the run inspector matches the canvas.</summary>
    IReadOnlyList<string> Order,
    /// <summary>Nodes grouped by layer. Used for layout and for parallel source fetching.</summary>
    IReadOnlyList<IReadOnlyList<string>> Waves,
    IReadOnlyDictionary<string, int> Layer,
    IReadOnlyDictionary<string, List<PipelineLink>> Successors,
    IReadOnlyDictionary<string, List<PipelineLink>> Predecessors,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<string> DestinationIds)
{
    public PipelineNodeDef Node(string id) => Nodes[id];
    public PipelineNodeSpec Spec(string id) => Specs[id];

    /// <summary>The node feeding a given input port, or null when nothing does.</summary>
    public string? InputOn(string id, string port) =>
        Predecessors.GetValueOrDefault(id)?.FirstOrDefault(l => l.ToPort == port)?.Other;

    /// <summary>Every node feeding a port, in edge order. Meaningful for the multi-input steps.</summary>
    public IReadOnlyList<string> InputsOn(string id, string port) =>
        Predecessors.GetValueOrDefault(id)?.Where(l => l.ToPort == port).Select(l => l.Other).ToList() ?? [];

    /// <summary>
    /// The links feeding a port, rather than just the node ids.
    /// <para>
    /// Needed because a link's FROM port now decides which relation to read: an ordinary node writes one
    /// relation named after itself, but a switch writes one per output. Dropping the port here would make
    /// every branch of a switch read the same rows.
    /// </para>
    /// </summary>
    public IReadOnlyList<PipelineLink> LinksInto(string id, string port) =>
        Predecessors.GetValueOrDefault(id)?.Where(l => l.ToPort == port).ToList() ?? [];

    /// <summary>Nodes downstream of a node, transitively — the set to skip when it fails.</summary>
    public HashSet<string> Descendants(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var link in Successors.GetValueOrDefault(id) ?? [])
            if (seen.Add(link.Other)) queue.Enqueue(link.Other);

        while (queue.Count > 0)
            foreach (var link in Successors.GetValueOrDefault(queue.Dequeue()) ?? [])
                if (seen.Add(link.Other)) queue.Enqueue(link.Other);

        return seen;
    }

    /// <summary>Nodes upstream of a node, transitively — the set it cannot run without.</summary>
    public HashSet<string> Ancestors(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var link in Predecessors.GetValueOrDefault(id) ?? [])
            if (seen.Add(link.Other)) queue.Enqueue(link.Other);

        while (queue.Count > 0)
            foreach (var link in Predecessors.GetValueOrDefault(queue.Dequeue()) ?? [])
                if (seen.Add(link.Other)) queue.Enqueue(link.Other);

        return seen;
    }

    /// <summary>
    /// The set a partial run must actually execute to satisfy <paramref name="selection"/>: the selected
    /// nodes plus every ancestor they need, in this graph's topological order.
    /// <para>
    /// The ancestors are not a convenience — they are forced. A node's input is a DuckDB relation inside the
    /// run's scratch database, and that database is created per run and deleted when the run succeeds, so
    /// there is never a previous run's output for a mid-graph step to read. "Run this step" can only mean
    /// "run this step and whatever produces its input".
    /// </para>
    /// <para>
    /// A destination is terminal, so it has no successors and can never be pulled in as somebody else's
    /// ancestor. That is what makes a partial run safe by construction: a write or an email happens only if
    /// the node was selected deliberately.
    /// </para>
    /// <para>Returns every node when the selection is empty — no selection means the whole pipeline.</para>
    /// </summary>
    public IReadOnlyList<string> ClosureFor(IEnumerable<string>? selection)
    {
        if (selection is null) return Order;

        var chosen = new HashSet<string>(selection.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.Ordinal);

        if (chosen.Count == 0) return Order;

        var needed = new HashSet<string>(chosen, StringComparer.Ordinal);
        foreach (var id in chosen)
        {
            if (!Nodes.ContainsKey(id)) continue;
            needed.UnionWith(Ancestors(id));
        }

        // Ordered by Order, never by the caller's iteration order: the walk depends on topological order,
        // and a selection arrives from a UI as an arbitrary set.
        return Order.Where(needed.Contains).ToList();
    }

    /// <summary>
    /// Selected ids that are not nodes in this graph. A partial run is refused rather than silently run
    /// against a smaller set — a stale selection would otherwise quietly execute something other than what
    /// was asked for.
    /// </summary>
    public IReadOnlyList<string> UnknownIds(IEnumerable<string>? selection) =>
        selection?.Where(id => !string.IsNullOrWhiteSpace(id) && !Nodes.ContainsKey(id)).ToList() ?? [];

    /// <summary>
    /// True when this graph cannot run unattended, because a file source expects a run-time upload.
    /// The service checks this before letting a cron or the API trigger be enabled.
    /// </summary>
    public bool RequiresManualRun => Nodes.Values.Any(n =>
        n.Type == PipelineNodeTypes.SourceFile &&
        (n.Config?["location"] as JsonValue)?.GetValue<string>() == PipelineFileLocations.Upload);
}
