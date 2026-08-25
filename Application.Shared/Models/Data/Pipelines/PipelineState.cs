using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Data.Pipelines;

/// <summary>
/// What a pipeline remembers between runs. Today that is one incremental watermark per source step.
/// <para>
/// Keyed on <b>node</b>, not pipeline: a pipeline reading two tables incrementally needs two independent
/// watermarks, and putting a single value on <c>pipeline</c> would silently make the second source reuse the
/// first one's high-water mark.
/// </para>
/// <para>
/// Deliberately not reusing <c>IngestionSource.IncrementalLastValue</c>. That column belongs to a different
/// feature's row and has different semantics — it is written from the destination after loading, while this
/// is captured from the source before loading. Sharing storage would force the two to agree on semantics
/// they do not agree on.
/// </para>
/// </summary>
public class PipelineState : BaseModel
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [MaxLength(450)]
    public string PipelineId { get; set; } = string.Empty;

    /// <summary>The source step this watermark belongs to.</summary>
    [Required]
    [MaxLength(200)]
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// The highest value seen, as text. Text rather than a typed column because the watermark column can be
    /// an integer, a date, a timestamp or a string, and one nullable column per type would be worse than
    /// round-tripping through invariant text — which is what the source query has to embed anyway.
    /// </summary>
    [MaxLength(400)]
    public string? WatermarkValue { get; set; }

    /// <summary>
    /// DuckDB type name the value came back as, so a later run can quote it the same way and a human can
    /// see why a comparison behaved oddly.
    /// </summary>
    [MaxLength(64)]
    public string? WatermarkType { get; set; }

    /// <summary>Rows the last successful run of this step read. Diagnostic — a sudden 0 is worth seeing.</summary>
    public long? RowsLastRun { get; set; }

    /// <summary>When the watermark last advanced. Null means it has never run successfully.</summary>
    public DateTime? AdvancedAt { get; set; }

    /// <summary>The run that last advanced it, for tracing back to the step's SQL.</summary>
    [MaxLength(450)]
    public string? AdvancedByRunId { get; set; }
}
