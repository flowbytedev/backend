using Application.Shared.Models.Data;

namespace Application.Shared.Services.Data;

/// <summary>
/// Orchestrates moving a table between datasets: the physical DuckDB data move, re-pointing any
/// table-scoped sharing grants to the destination, and emailing affected users. Never throws — errors
/// are returned via MoveTableOutcome.Error.
/// </summary>
public interface IDatasetTableMoveService
{
    Task<MoveTableOutcome> MoveTableAsync(
        string companyId,
        string sourceDatasetId,
        string tableName,
        string targetDatasetId,
        string movedByUserId,
        string movedByName,
        CancellationToken ct = default);
}
