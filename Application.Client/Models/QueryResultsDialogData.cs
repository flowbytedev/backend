using Application.Shared.Models;

namespace Application.Client.Models;

public class QueryResultsDialogData
{
    public Metric Metric { get; set; } = new();
    public List<MetricFilterValue>? FilterValues { get; set; }
}
