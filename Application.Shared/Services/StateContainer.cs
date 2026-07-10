using Application.Shared.Models;

namespace Application.Shared.Services;

public class StateContainer
{
    private string? savedString;

    private Company company;


    public Company Company
    {
        get => company;
        set
        {
            company = value;
            NotifyStateChanged();
        }
    }


    public string Property
    {
        get => savedString ?? string.Empty;
        set
        {
            savedString = value;
            NotifyStateChanged();
        }
    }

    private string? notebookExplorerNotebookId;

    /// <summary>
    /// Set by the Query Notebook page while a notebook is open — tells NavMenu to swap its usual menu
    /// for a MotherDuck-style dataset/table explorer. Null means "show the normal navigation".
    /// </summary>
    public string? NotebookExplorerNotebookId
    {
        get => notebookExplorerNotebookId;
        set
        {
            if (notebookExplorerNotebookId == value) return;
            notebookExplorerNotebookId = value;
            NotifyStateChanged();
        }
    }

    public event Action? OnChange;

    /// <summary>
    /// Fired by the DatabaseExplorerTree (sidebar) after it creates a cell in the currently-open notebook
    /// directly via the API — the Query Notebook page subscribes to reload its in-memory cell list, since
    /// the tree and the page are separate components with no parent/child relationship.
    /// </summary>
    public event Action? NotebookCellsChangedExternally;
    public void NotifyNotebookCellsChangedExternally() => NotebookCellsChangedExternally?.Invoke();

    private void NotifyStateChanged() => OnChange?.Invoke();
}
