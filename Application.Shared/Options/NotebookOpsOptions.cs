namespace Application.Shared.Options;

/// <summary>Static ops distribution list for notebook alerts — mirrors <c>SalesSnapshotEmailOptions</c>'
/// static-recipient pattern rather than resolving a per-notebook "audience" (a notebook has no natural
/// audience the way a monitored entity does).</summary>
public class NotebookOpsOptions
{
    public List<string> FailureRecipients { get; set; } = new();
}
