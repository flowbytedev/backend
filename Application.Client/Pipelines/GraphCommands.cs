using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Shared.Models.Data.Pipelines;
using Application.Shared.Services.Data.Pipelines;

namespace Application.Client.Pipelines;

/// <summary>
/// Undo/redo for graph edits. Ported from relay's <c>GraphCommands</c>.
/// <para>
/// Because positions are <em>derived</em> by <see cref="PipelineLayout"/> rather than stored, no command here
/// ever records a coordinate — undo is purely semantic. That is the third payoff of computing layout instead
/// of persisting it (the others being that the editor and the run view agree exactly, and that a hand-edited
/// YAML file is not mostly coordinates).
/// </para>
/// </summary>
public interface IGraphCommand
{
    string Label { get; }
    void Apply(PipelineGraph graph);
    void Revert(PipelineGraph graph);
}

/// <summary>
/// The editor's edit history. Every mutation goes through <see cref="Execute"/>, so undo, redo and the dirty
/// flag stay consistent without each call site remembering to record anything.
/// </summary>
public sealed class GraphHistory
{
    private const int MaxDepth = 100;

    /// <summary>Consecutive edits to the same field inside this window merge into one undo step.</summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(600);

    private readonly Stack<IGraphCommand> _undo = new();
    private readonly Stack<IGraphCommand> _redo = new();

    private string? _lastCoalesceKey;
    private DateTime _lastEditUtc;
    private int _savedDepth;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty => _undo.Count != _savedDepth;

    public string? NextUndoLabel => _undo.Count > 0 ? _undo.Peek().Label : null;
    public string? NextRedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;

    /// <summary>
    /// Applies a command and records it. <paramref name="coalesceKey"/> merges rapid edits to the same field
    /// — without it, typing a step name becomes forty undo steps.
    /// </summary>
    public void Execute(
        PipelineGraph graph, IGraphCommand command, string? coalesceKey = null, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;

        command.Apply(graph);
        _redo.Clear();

        var canMerge = coalesceKey is not null
                       && coalesceKey == _lastCoalesceKey
                       && now - _lastEditUtc <= CoalesceWindow
                       && _undo.Count > 0
                       && _undo.Peek() is SetNodeConfigCommand top
                       && command is SetNodeConfigCommand incoming
                       && top.NodeId == incoming.NodeId;

        if (canMerge)
        {
            // Keep the original "before" so one undo returns to where the burst started.
            var previous = (SetNodeConfigCommand)_undo.Pop();
            var current = (SetNodeConfigCommand)command;
            _undo.Push(new SetNodeConfigCommand(current.NodeId, previous.Before, current.After, current.Label));
        }
        else
        {
            _undo.Push(command);
            if (_undo.Count > MaxDepth) TrimOldest();
        }

        _lastCoalesceKey = coalesceKey;
        _lastEditUtc = now;
    }

    public string? Undo(PipelineGraph graph)
    {
        if (_undo.Count == 0) return null;
        var command = _undo.Pop();
        command.Revert(graph);
        _redo.Push(command);
        _lastCoalesceKey = null;
        return command.Label;
    }

    public string? Redo(PipelineGraph graph)
    {
        if (_redo.Count == 0) return null;
        var command = _redo.Pop();
        command.Apply(graph);
        _undo.Push(command);
        _lastCoalesceKey = null;
        return command.Label;
    }

    /// <summary>Marks the current position as saved, so <see cref="IsDirty"/> goes false.</summary>
    public void MarkSaved()
    {
        _savedDepth = _undo.Count;
        _lastCoalesceKey = null;
    }

    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _savedDepth = 0;
        _lastCoalesceKey = null;
    }

    private void TrimOldest()
    {
        // Stack has no bottom-removal, so rebuild without the oldest entry.
        var kept = _undo.ToArray().Take(MaxDepth).Reverse().ToList();
        _undo.Clear();
        foreach (var command in kept) _undo.Push(command);
        if (_savedDepth > _undo.Count) _savedDepth = -1;   // the save point fell off history: stay dirty
    }
}

// ============================================================================ commands

public sealed class AddNodeCommand(PipelineNodeDef node, IReadOnlyList<PipelineEdgeDef>? edges = null)
    : IGraphCommand
{
    public string Label => $"Add {node.Label ?? node.Type}";

    public void Apply(PipelineGraph graph)
    {
        graph.Nodes.Add(node);
        if (edges is not null) graph.Edges.AddRange(edges);
    }

    public void Revert(PipelineGraph graph)
    {
        graph.Nodes.RemoveAll(n => n.Id == node.Id);
        if (edges is not null)
            foreach (var edge in edges) graph.Edges.RemoveAll(e => e.Id == edge.Id);
    }
}

/// <summary>
/// Deletes steps together with every incident edge, and optionally heals the gap by reconnecting a step's
/// single predecessor to its single successor. In a mostly-linear pipeline that healing is the difference
/// between pleasant and infuriating.
/// </summary>
public sealed class DeleteNodesCommand : IGraphCommand
{
    // Positions are recorded so undo is an exact inverse. Document order is the layout's deterministic
    // tie-breaker for sibling ordering, so restoring a mid-graph step at the end of the list would change
    // the geometry that an undo is supposed to put back.
    private readonly List<(int Index, PipelineNodeDef Node)> _nodes;
    private readonly List<(int Index, PipelineEdgeDef Edge)> _removedEdges;
    private readonly List<PipelineEdgeDef> _healEdges;

    public DeleteNodesCommand(PipelineGraph graph, IEnumerable<string> nodeIds, bool heal)
    {
        var ids = nodeIds.ToHashSet(StringComparer.Ordinal);

        _nodes = graph.Nodes
            .Select((node, index) => (Index: index, Node: node))
            .Where(pair => ids.Contains(pair.Node.Id))
            .ToList();

        _removedEdges = graph.Edges
            .Select((edge, index) => (Index: index, Edge: edge))
            .Where(pair => ids.Contains(pair.Edge.From) || ids.Contains(pair.Edge.To))
            .ToList();

        _healEdges = [];

        if (!heal) return;

        foreach (var id in ids)
        {
            var incoming = _removedEdges.Select(p => p.Edge)
                .Where(e => e.To == id && !ids.Contains(e.From)).ToList();
            var outgoing = _removedEdges.Select(p => p.Edge)
                .Where(e => e.From == id && !ids.Contains(e.To)).ToList();

            // Only heal the unambiguous case: exactly one in, exactly one out. Deleting a join, which has
            // two inputs, therefore heals nothing — there is no honest answer to which side survives.
            if (incoming.Count != 1 || outgoing.Count != 1) continue;

            _healEdges.Add(new PipelineEdgeDef
            {
                Id = Guid.NewGuid().ToString("N"),
                From = incoming[0].From,
                FromPort = incoming[0].FromPort,
                To = outgoing[0].To,
                ToPort = outgoing[0].ToPort
            });
        }
    }

    public string Label => _nodes.Count == 1
        ? $"Delete {_nodes[0].Node.Label ?? _nodes[0].Node.Id}"
        : $"Delete {_nodes.Count} steps";

    public void Apply(PipelineGraph graph)
    {
        foreach (var (_, node) in _nodes) graph.Nodes.RemoveAll(n => n.Id == node.Id);
        foreach (var (_, edge) in _removedEdges) graph.Edges.RemoveAll(e => e.Id == edge.Id);
        graph.Edges.AddRange(_healEdges);

        foreach (var (_, node) in _nodes)
        {
            graph.Layout?.Pins.Remove(node.Id);
            // The cached schema goes too, or a step re-created with the same id would inherit stale columns.
            graph.Schemas?.Remove(node.Id);
        }
    }

    public void Revert(PipelineGraph graph)
    {
        foreach (var edge in _healEdges) graph.Edges.RemoveAll(e => e.Id == edge.Id);

        // Ascending index, so each insert lands at the position it originally occupied.
        foreach (var (index, node) in _nodes.OrderBy(p => p.Index))
            graph.Nodes.Insert(Math.Min(index, graph.Nodes.Count), node);

        foreach (var (index, edge) in _removedEdges.OrderBy(p => p.Index))
            graph.Edges.Insert(Math.Min(index, graph.Edges.Count), edge);
    }
}

public sealed class AddEdgeCommand(PipelineEdgeDef edge) : IGraphCommand
{
    public string Label => "Connect";
    public void Apply(PipelineGraph graph) => graph.Edges.Add(edge);
    public void Revert(PipelineGraph graph) => graph.Edges.RemoveAll(e => e.Id == edge.Id);
}

/// <summary>
/// Connects two steps, first removing anything already feeding the target port.
/// <para>
/// This replacement is the pipeline-specific part. Most step types accept exactly one edge per input port,
/// and the compiler rejects a second one — so dragging a new connection onto an occupied port has to mean
/// "re-route this input", not "create an error the author then has to find and fix".
/// </para>
/// </summary>
public sealed class ConnectCommand : IGraphCommand
{
    private readonly PipelineEdgeDef _edge;
    private readonly List<(int Index, PipelineEdgeDef Edge)> _displaced;

    public ConnectCommand(PipelineGraph graph, PipelineEdgeDef edge, bool replaceExisting)
    {
        _edge = edge;
        _displaced = replaceExisting
            ? graph.Edges
                .Select((e, index) => (Index: index, Edge: e))
                .Where(p => p.Edge.To == edge.To && p.Edge.ToPort == edge.ToPort)
                .ToList()
            : [];
    }

    public string Label => _displaced.Count > 0 ? "Reconnect" : "Connect";

    public void Apply(PipelineGraph graph)
    {
        foreach (var (_, edge) in _displaced) graph.Edges.RemoveAll(e => e.Id == edge.Id);
        graph.Edges.Add(_edge);
    }

    public void Revert(PipelineGraph graph)
    {
        graph.Edges.RemoveAll(e => e.Id == _edge.Id);
        foreach (var (index, edge) in _displaced.OrderBy(p => p.Index))
            graph.Edges.Insert(Math.Min(index, graph.Edges.Count), edge);
    }
}

public sealed class DeleteEdgeCommand : IGraphCommand
{
    private readonly PipelineEdgeDef? _edge;
    private readonly int _index;

    public DeleteEdgeCommand(PipelineGraph graph, string edgeId)
    {
        _index = graph.Edges.FindIndex(e => e.Id == edgeId);
        _edge = _index >= 0 ? graph.Edges[_index] : null;
    }

    public string Label => "Disconnect";

    public void Apply(PipelineGraph graph)
    {
        if (_edge is not null) graph.Edges.RemoveAll(e => e.Id == _edge.Id);
    }

    public void Revert(PipelineGraph graph)
    {
        // Restored at its original index, for the same determinism reason as DeleteNodesCommand.
        if (_edge is not null) graph.Edges.Insert(Math.Min(Math.Max(_index, 0), graph.Edges.Count), _edge);
    }
}

/// <summary>
/// Splices a step onto an existing edge: the old edge is replaced by two new ones. This backs the
/// insert-on-edge affordance, which is the primary way to add a step and needs no drag code at all.
/// </summary>
public sealed class SpliceNodeCommand : IGraphCommand
{
    private readonly PipelineNodeDef _node;
    private readonly PipelineEdgeDef? _replaced;

    // The index the replaced edge occupied. Restoring it there rather than appending matters because edge
    // order is one of the layout's deterministic tie-breakers, so an undo that puts the edge back at the end
    // would quietly change the geometry the undo was supposed to restore. (Relay's version appends; this is
    // a fix, not a port.)
    private readonly int _replacedIndex;

    private readonly PipelineEdgeDef _before;
    private readonly PipelineEdgeDef _after;

    public SpliceNodeCommand(PipelineGraph graph, string edgeId, PipelineNodeDef node)
    {
        _node = node;
        _replacedIndex = graph.Edges.FindIndex(e => e.Id == edgeId);
        _replaced = _replacedIndex >= 0 ? graph.Edges[_replacedIndex] : null;

        var inPorts = PipelineNodeCatalog.InPortsFor(node);
        var outPorts = PipelineNodeCatalog.OutPortsFor(node);

        _before = new PipelineEdgeDef
        {
            Id = Guid.NewGuid().ToString("N"),
            From = _replaced?.From ?? string.Empty,
            FromPort = _replaced?.FromPort ?? PipelinePorts.Out,
            To = node.Id,
            // The spliced step's FIRST input, which for a join is "left" rather than "in".
            ToPort = inPorts.Count > 0 ? inPorts[0] : PipelinePorts.In
        };
        _after = new PipelineEdgeDef
        {
            Id = Guid.NewGuid().ToString("N"),
            From = node.Id,
            FromPort = outPorts.Count > 0 ? outPorts[0] : PipelinePorts.Out,
            To = _replaced?.To ?? string.Empty,
            ToPort = _replaced?.ToPort ?? PipelinePorts.In
        };
    }

    public string Label => $"Insert {_node.Label ?? _node.Type}";

    public void Apply(PipelineGraph graph)
    {
        if (_replaced is not null) graph.Edges.RemoveAll(e => e.Id == _replaced.Id);
        graph.Nodes.Add(_node);
        graph.Edges.Add(_before);
        graph.Edges.Add(_after);
    }

    public void Revert(PipelineGraph graph)
    {
        graph.Edges.RemoveAll(e => e.Id == _before.Id || e.Id == _after.Id);
        graph.Nodes.RemoveAll(n => n.Id == _node.Id);
        if (_replaced is not null)
            graph.Edges.Insert(Math.Min(Math.Max(_replacedIndex, 0), graph.Edges.Count), _replaced);
    }
}

/// <summary>
/// Replaces a step wholesale. Storing the entire node JSON either side is the simplest correct approach —
/// per-field diffing buys nothing when a step is a few hundred bytes.
/// </summary>
public sealed class SetNodeConfigCommand(string nodeId, string before, string after, string label)
    : IGraphCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string NodeId { get; } = nodeId;
    public string Before { get; } = before;
    public string After { get; } = after;
    public string Label { get; } = label;

    public void Apply(PipelineGraph graph) => Replace(graph, After);
    public void Revert(PipelineGraph graph) => Replace(graph, Before);

    private void Replace(PipelineGraph graph, string json)
    {
        var index = graph.Nodes.FindIndex(n => n.Id == NodeId);
        if (index < 0) return;

        // The inspector mutates the node in place, so Apply is usually a no-op. Swapping in a fresh object
        // anyway would give the inspector a new Node reference on every keystroke and reset the caret in the
        // focused input, so only replace when the graph genuinely differs.
        if (Snapshot(graph.Nodes[index]) == json) return;

        var restored = JsonSerializer.Deserialize<PipelineNodeDef>(json, Json);
        if (restored is not null) graph.Nodes[index] = restored;
    }

    /// <summary>Snapshots a step so a later edit can be recorded against it.</summary>
    public static string Snapshot(PipelineNodeDef node) => JsonSerializer.Serialize(node, Json);
}

/// <summary>Inserts a pasted subgraph as a single undoable step.</summary>
public sealed class PasteSubgraphCommand(List<PipelineNodeDef> nodes, List<PipelineEdgeDef> edges)
    : IGraphCommand
{
    public string Label => nodes.Count == 1 ? "Paste step" : $"Paste {nodes.Count} steps";

    public void Apply(PipelineGraph graph)
    {
        graph.Nodes.AddRange(nodes);
        graph.Edges.AddRange(edges);
    }

    public void Revert(PipelineGraph graph)
    {
        foreach (var edge in edges) graph.Edges.RemoveAll(e => e.Id == edge.Id);
        foreach (var node in nodes) graph.Nodes.RemoveAll(n => n.Id == node.Id);
    }
}

/// <summary>
/// Records manual positions. Pins live in <c>graph.Layout.Pins</c>, deliberately apart from the nodes, so
/// the YAML view can omit them — a document that is mostly coordinates is not one anybody hand-edits.
/// </summary>
public sealed class SetPinsCommand : IGraphCommand
{
    private readonly Dictionary<string, PipelinePin> _before;
    private readonly Dictionary<string, PipelinePin> _after;

    public SetPinsCommand(PipelineGraph graph, Dictionary<string, PipelinePin> after, string label)
    {
        _before = graph.Layout?.Pins is { } pins
            ? new Dictionary<string, PipelinePin>(pins, StringComparer.Ordinal)
            : new Dictionary<string, PipelinePin>(StringComparer.Ordinal);
        _after = after;
        Label = label;
    }

    public string Label { get; }

    public void Apply(PipelineGraph graph) => Set(graph, _after);
    public void Revert(PipelineGraph graph) => Set(graph, _before);

    private static void Set(PipelineGraph graph, Dictionary<string, PipelinePin> pins)
    {
        graph.Layout ??= new PipelineLayoutHints();
        graph.Layout.Pins = new Dictionary<string, PipelinePin>(pins, StringComparer.Ordinal);
    }
}

/// <summary>Renames a step, rewriting every edge that referenced the old id.</summary>
public sealed class RenameNodeCommand(string oldId, string newId) : IGraphCommand
{
    public string Label => "Rename step";

    public void Apply(PipelineGraph graph) => Rename(graph, oldId, newId);
    public void Revert(PipelineGraph graph) => Rename(graph, newId, oldId);

    /// <summary>
    /// A step's id is also its relation name in generated SQL and its handle in a YAML <c>from:</c> list, so
    /// a rename has to carry the edges, the pins and the cached schema with it or the graph silently breaks.
    /// </summary>
    private static void Rename(PipelineGraph graph, string from, string to)
    {
        var node = graph.Nodes.FirstOrDefault(n => n.Id == from);
        if (node is null || graph.Nodes.Any(n => n.Id == to)) return;

        node.Id = to;

        foreach (var edge in graph.Edges)
        {
            if (edge.From == from) edge.From = to;
            if (edge.To == from) edge.To = to;
        }

        if (graph.Layout?.Pins.Remove(from, out var pin) == true) graph.Layout.Pins[to] = pin;

        if (graph.Schemas is not null && graph.Schemas.Remove(from, out var columns))
            graph.Schemas[to] = columns;
    }
}

// ============================================================================ factory

/// <summary>Factory for new step instances, seeded with the catalogue's sensible defaults.</summary>
public static class NodeFactory
{
    public static PipelineNodeDef Create(string type, IEnumerable<string> existingIds)
    {
        var spec = PipelineNodeCatalog.Get(type);
        var config = new JsonObject();

        // Seed the values a step is useless without, so a freshly added step is not instantly invalid for
        // reasons the author cannot see. Anything genuinely requiring a choice (which dataset, which file) is
        // deliberately left empty — a guessed default there would be worse than a visible blank.
        switch (type)
        {
            case PipelineNodeTypes.SourceFile:
                config["location"] = PipelineFileLocations.Folder;
                config["format"] = "csv";
                config["pick"] = "newest";
                config["hasHeader"] = true;
                break;

            case PipelineNodeTypes.SourceDatabase:
                config["mode"] = "table";
                break;

            case PipelineNodeTypes.TransformRank:
                config["method"] = "rank";
                config["column"] = "rank";
                break;

            case PipelineNodeTypes.TransformSurrogateKey:
                config["column"] = "row_key";
                config["startAt"] = 1;
                break;

            case PipelineNodeTypes.TransformFill:
                config["direction"] = "down";
                break;

            case PipelineNodeTypes.TransformSplit:
                config["mode"] = "columns";
                config["delimiter"] = ",";
                break;

            case PipelineNodeTypes.TransformPivot:
                config["function"] = "sum";
                break;

            case PipelineNodeTypes.TransformUnpivot:
                config["nameColumn"] = "attribute";
                config["valueColumn"] = "value";
                break;

            case PipelineNodeTypes.TransformWindow:
                // Off by default: a running total is the less common of the two, and silently accumulating
                // when someone wanted a group total is a wrong number rather than a visible mistake.
                config["cumulative"] = false;
                break;

            case PipelineNodeTypes.SourceApi:
                config["method"] = "GET";
                // Unpaginated by default: guessing a pagination style would send parameters the endpoint
                // does not understand, and a single request is the one shape that always means something.
                config["pagination"] = PipelineApiPagination.None;
                config["flatten"] = PipelineApiFlatten.OneLevel;
                break;

            case PipelineNodeTypes.TransformMap:
                config["columns"] = new JsonObject();
                config["keepUnmapped"] = false;
                break;

            case PipelineNodeTypes.TransformCompute:
                config["columns"] = new JsonObject();
                break;

            case PipelineNodeTypes.TransformJoin:
                config["kind"] = "left";
                config["on"] = new JsonArray(new JsonObject { ["left"] = "", ["right"] = "" });
                config["bring"] = new JsonObject();
                break;

            case PipelineNodeTypes.TransformUnion:
                config["mode"] = "all";
                config["byName"] = true;
                break;

            case PipelineNodeTypes.TransformDedupe:
                config["keys"] = new JsonArray();
                config["keep"] = "first";
                break;

            case PipelineNodeTypes.TransformAggregate:
                config["groupBy"] = new JsonArray();
                config["metrics"] = new JsonArray(
                    new JsonObject { ["name"] = "", ["function"] = PipelineAggregateFunctions.Sum, ["column"] = "" });
                break;

            case PipelineNodeTypes.DestinationDataset:
                config["mode"] = PipelineWriteModes.Append;
                // Creating a table is opt-in, and for an external dataset it means DDL in someone else's
                // database — never a default.
                config["createIfMissing"] = false;
                break;

            case PipelineNodeTypes.DestinationApi:
                config["method"] = "POST";
                config["shape"] = PipelineApiWriteShapes.Batch;
                config["batchSize"] = 500;
                // Stop on the first failure. There is no rollback over HTTP, so the choice is between
                // stopping and pushing more rows at an endpoint that has already rejected one.
                config["stopOnError"] = true;
                break;
        }

        return new PipelineNodeDef
        {
            Id = UniqueId(type, existingIds),
            Type = type,
            Label = spec?.Label ?? type,
            Config = config
        };
    }

    /// <summary>
    /// A readable, unique id derived from the type. Ids become relation names in generated SQL and handles in
    /// a YAML <c>from:</c> list, so they must be plain and stable enough to type by hand.
    /// </summary>
    public static string UniqueId(string type, IEnumerable<string> existingIds)
    {
        var taken = existingIds.ToHashSet(StringComparer.Ordinal);

        var stem = type.Contains('.') ? type[(type.IndexOf('.') + 1)..] : type;
        var clean = new string(stem.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        if (clean.Length == 0) clean = "step";

        if (taken.Add(clean)) return clean;

        for (var n = 2; n < 1000; n++)
            if (!taken.Contains($"{clean}{n}")) return $"{clean}{n}";

        return "step" + Guid.NewGuid().ToString("N")[..6];
    }
}
