using System;
using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Data;

/// <summary>Where a scheduled ingestion pulls its data from.</summary>
public enum IngestionSourceKind
{
    ExternalDatabase,
    Rest,
    Blob,
    Sftp,
    // Re-runs a SELECT against the dataset's own DuckDB tables on a schedule (e.g. a Query Notebook
    // cell's query), refreshing a target table in place. No fetch/temp file — see
    // IDuckdbService.RunQueryIntoTableAsync.
    SqlQuery
}

/// <summary>
/// A configured, schedulable data pull into a dataset table. Enum-ish fields
/// (<see cref="SourceKind"/>, <see cref="ImportMode"/>, <see cref="LastRunStatus"/>) are stored as
/// strings; <see cref="SourceConfig"/> holds kind-specific JSON; secrets live encrypted in
/// <see cref="SecretEncrypted"/> (never returned to the client).
/// </summary>
public class IngestionSource
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string CompanyId { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;

    [Required]
    [StringLength(150)]
    public string TargetTable { get; set; } = string.Empty;

    [Required]
    [StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    // IngestionSourceKind as string.
    [Required]
    [StringLength(40)]
    public string SourceKind { get; set; } = nameof(IngestionSourceKind.ExternalDatabase);

    // For ExternalDatabase sources: the configured Database entity (StatusDbContext) whose connection is reused.
    public string? SourceEntityId { get; set; }

    // Kind-specific JSON (see IngestionSourceConfig).
    public string? SourceConfig { get; set; }

    // Encrypted credential for REST/Blob/SFTP sources (ICredentialProtector). DB sources reuse the
    // stored DatabaseConnection secret, so this stays null for them.
    public string? SecretEncrypted { get; set; }

    // ImportMode as string (Append/Replace/Upsert).
    [Required]
    [StringLength(20)]
    public string ImportMode { get; set; } = nameof(Data.ImportMode.Append);

    // Comma-separated key columns for Upsert.
    public string? KeyColumns { get; set; }

    // When the target table is absent at run time, create it from the fetched data's inferred schema.
    public bool CreateIfNotExists { get; set; } = true;

    // Optional watermark column for incremental loads, and the last value seen.
    [StringLength(150)]
    public string? IncrementalColumn { get; set; }
    public string? IncrementalLastValue { get; set; }

    [Required]
    [StringLength(120)]
    public string CronExpression { get; set; } = "0 * * * *";

    [StringLength(80)]
    public string? TimeZone { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime? LastRunAt { get; set; }
    [StringLength(40)]
    public string? LastRunStatus { get; set; }
    public string? LastRunMessage { get; set; }
    public int? LastRunRows { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>One execution of an <see cref="IngestionSource"/> (run history).</summary>
public class IngestionRun
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string SourceId { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    [Required]
    [StringLength(40)]
    public string Status { get; set; } = "Running";

    public int? RowsIngested { get; set; }
    public string? ErrorMessage { get; set; }

    // The Hangfire background-job id when this run was executed via Hangfire (batch / scheduled). Null for
    // inline "Run now". Lets the UI deep-link to the Hangfire dashboard job details.
    [StringLength(100)]
    public string? JobId { get; set; }

    // Captured processing log (the progress/console lines), so the run's log can be viewed in-app
    // without opening the Hangfire dashboard.
    public string? Log { get; set; }
}
