using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Application.Shared.Models.Data.Pipelines;

/// <summary>
/// The graph document stored in <c>Pipeline.GraphJson</c>. One document drives the editor, the YAML view,
/// the validator and the executor.
/// <para>
/// Node <em>ports</em> are deliberately absent from the document: they come from
/// <see cref="Services.Data.Pipelines.PipelineNodeCatalog"/>, keyed by node type. That way a catalogue
/// change can never leave a stored graph describing ports that no longer exist.
/// </para>
/// <para>
/// Two other things are deliberately kept out of <see cref="PipelineNodeDef"/> and parked in their own
/// sidecar objects — <see cref="Layout"/> and <see cref="Schemas"/>. Both are derived or cosmetic, and
/// both change constantly. Keeping them off the node definitions means the YAML view and any future
/// version diff can exclude one object each instead of filtering a field on every node.
/// </para>
/// </summary>
public sealed class PipelineGraph
{
    /// <summary>Highest document version this build understands. Bumped only on a breaking contract change.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public PipelineSettings Settings { get; set; } = new();

    /// <summary>Editor pan/zoom. Purely cosmetic; the executor ignores it.</summary>
    public PipelineViewport? Viewport { get; set; }

    /// <summary>Manual position overrides. Absent node ids are auto-placed by the layout engine.</summary>
    public PipelineLayoutHints? Layout { get; set; }

    /// <summary>
    /// Node id -> the columns that node produced the last time it was previewed or run. A <em>cache</em>,
    /// so the editor can populate a mapping grid and flag drift without hitting every source again.
    /// <para>
    /// It never authorizes anything. The executor always re-reads the real relation's columns, because
    /// this cache can be arbitrarily stale — the whole point of the schema-drift check is that upstream
    /// changed without telling us.
    /// </para>
    /// </summary>
    public Dictionary<string, List<PipelineColumn>>? Schemas { get; set; }

    public List<PipelineNodeDef> Nodes { get; set; } = new();
    public List<PipelineEdgeDef> Edges { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Parses a stored graph, returning null (never throwing) on malformed JSON.</summary>
    public static PipelineGraph? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PipelineGraph>(json, Json); }
        catch (JsonException) { return null; }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// What "New pipeline" creates: genuinely empty. Unlike a workflow, a pipeline has no trigger node to
    /// seed — its entry points are its sources, and guessing which kind of source the user wants would
    /// just make a node they have to delete.
    /// </summary>
    public static PipelineGraph NewDefault() => new();

    public PipelineNodeDef? Node(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>The cached columns for a node, or an empty list when it has never been previewed.</summary>
    public List<PipelineColumn> SchemaFor(string nodeId) =>
        Schemas is not null && Schemas.TryGetValue(nodeId, out var cols) ? cols : new();
}

/// <summary>Run-wide execution settings. Per-node values override where applicable.</summary>
public sealed class PipelineSettings
{
    /// <summary>
    /// Ceiling for the whole run. Generous by ETL standards on purpose — a nightly load over millions of
    /// rows is normal, and a limit tuned for an interactive query would kill healthy runs.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 7200;

    /// <summary>Default <see cref="PipelineErrorMode"/> for nodes that do not set their own.</summary>
    public string OnError { get; set; } = PipelineErrorMode.Fail;

    /// <summary>
    /// When true, a source that returns zero rows fails the run. Off by default because an empty
    /// incremental window is normal; turn it on when a truly empty source means the upstream export
    /// broke, which is the more common reading for a full daily extract.
    /// </summary>
    public bool FailOnEmptySource { get; set; }

    /// <summary>
    /// How many steps may run at the same time.
    /// <para>
    /// A ceiling, not an instruction. Steps run concurrently only when they are put in the same
    /// <see cref="PipelineNodeDef.ParallelGroup"/>, so a graph that names no groups runs exactly as it
    /// always has however high this is set. Raising it never makes two steps overlap that the author did
    /// not say could overlap.
    /// </para>
    /// <para>
    /// Capped by the deployment's own limit, so a graph author cannot ask a scheduler for more
    /// concurrency than the operator allowed.
    /// </para>
    /// </summary>
    public int MaxParallelSteps { get; set; } = 4;

    /// <summary>
    /// Freshness policy applied to every node that does not set its own. A pipeline-wide default because
    /// the alternative is annotating twenty-five nodes to say the same thing, which nobody does — and an
    /// unannotated node is an unchecked one.
    /// <para>
    /// Individual nodes opt out with <c>freshness: { enabled: false }</c>.
    /// </para>
    /// </summary>
    public PipelineFreshnessPolicy? Freshness { get; set; }
}

public sealed class PipelineViewport
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Zoom { get; set; } = 1;
}

public sealed class PipelineLayoutHints
{
    /// <summary>Node id -> pinned position. Absent ids are auto-placed.</summary>
    public Dictionary<string, PipelinePin> Pins { get; set; } = new();
}

public sealed class PipelinePin
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
/// A column as a pipeline sees it: a name and a DuckDB type name. Deliberately not the existing
/// <see cref="Column"/> model — that one carries nullability, defaults and primary-key flags, which are
/// table-definition concerns, whereas an intermediate relation only ever has a name and a type.
/// </summary>
public sealed class PipelineColumn
{
    public string Name { get; set; } = string.Empty;

    /// <summary>DuckDB type name as reported by <c>DESCRIBE</c>, e.g. <c>VARCHAR</c>, <c>BIGINT</c>.</summary>
    public string Type { get; set; } = "VARCHAR";

    public PipelineColumn() { }

    public PipelineColumn(string name, string type)
    {
        Name = name;
        Type = type;
    }
}

/// <summary>One step in the graph.</summary>
public sealed class PipelineNodeDef
{
    /// <summary>
    /// Stable identifier, and the name used for this node's relation in the scratch database. The compiler
    /// restricts it to letters, digits, underscore and hyphen so it is safe to embed in generated SQL
    /// after quoting, and so YAML can name it in a <c>from:</c> list without quoting rules getting involved.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Catalogue type key, e.g. <c>transform.map</c>. See <see cref="PipelineNodeTypes"/>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Display name. Falls back to the catalogue label when empty.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Type-specific configuration, shape defined by the catalogue's field descriptors. String values may
    /// contain <c>{{ run.* }}</c> tokens, which are substituted at run time.
    /// </summary>
    public JsonObject? Config { get; set; }

    /// <summary>
    /// Names the set of steps this one may run alongside. Steps sharing a group name run concurrently
    /// once their inputs are ready; a step with no group runs on its own, which is the default and the
    /// behaviour every existing pipeline has.
    /// <para>
    /// Opt-in, and by <em>group</em> rather than by a simple "parallel" flag, because the reason two
    /// steps cannot overlap is usually that they touch the same table — and that is a fact about a
    /// particular pair, not about a step. A flag could only say "A may run with anything"; a group says
    /// "A may run with B", which is the thing an author actually knows. Two steps that must not overlap
    /// simply go in different groups, or in none.
    /// </para>
    /// <para>
    /// It never overrides the graph: a group is only ever consulted among steps that are <em>already</em>
    /// ready to run, so a step still waits for everything it reads from.
    /// </para>
    /// </summary>
    public string? ParallelGroup { get; set; }

    public PipelineRetryDef? Retry { get; set; }

    /// <summary>Per-node timeout. Null means only the run-level timeout applies.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>Overrides <see cref="PipelineSettings.OnError"/> for this node.</summary>
    public string? OnError { get; set; }

    /// <summary>
    /// Overrides <see cref="PipelineSettings.Freshness"/> for this node — a tighter deadline on the step
    /// somebody actually waits on, or <c>enabled: false</c> to exclude one the default should not cover.
    /// </summary>
    public PipelineFreshnessPolicy? Freshness { get; set; }
}

public sealed class PipelineRetryDef
{
    public int MaxAttempts { get; set; } = 1;

    /// <summary>Delay before attempt n is <c>BackoffMs</c> shifted left by (n - 1).</summary>
    public int BackoffMs { get; set; } = 500;
}

/// <summary>A directed connection between two node ports.</summary>
public sealed class PipelineEdgeDef
{
    public string Id { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string FromPort { get; set; } = PipelinePorts.Out;
    public string To { get; set; } = string.Empty;

    /// <summary>Target port — <c>in</c> for most nodes, <c>left</c> or <c>right</c> for a join.</summary>
    public string ToPort { get; set; } = PipelinePorts.In;

    /// <summary>Optional edge caption drawn by the editor.</summary>
    public string? Label { get; set; }
}
