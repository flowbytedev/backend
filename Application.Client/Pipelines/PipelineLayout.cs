using Application.Shared.Models.Data.Pipelines;
using Application.Shared.Services.Data.Pipelines;

namespace Application.Client.Pipelines;

/// <summary>
/// A hand-rolled layered graph layout (Sugiyama-lite): cycle breaking → layering → dummy nodes → crossing
/// reduction → coordinate assignment → edge routing.
/// <para>
/// Ported from the sibling relay app's <c>WorkflowLayout</c>, which solves the same problem in the same
/// stack. The geometry is entirely domain-agnostic, so almost none of it needed to change; the two things
/// that did are noted below.
/// </para>
/// <para>
/// Runs in the browser, in C#. That matters twice over: it re-runs on every structural edit, so a server
/// round trip per keystroke is out; and the editor and the run view must produce byte-identical geometry,
/// which they do by calling this one function.
/// </para>
/// <para>
/// <b>Node sizes are fixed functions of node kind and are never measured.</b> Measuring text would mean a
/// JS round trip per node followed by a second layout pass. Labels truncate with CSS ellipsis inside a
/// fixed box instead — which is also why renaming a step never re-runs layout.
/// </para>
/// <para>
/// <b>Determinism is a hard requirement.</b> Nothing that affects output may depend on <c>Dictionary</c> or
/// <c>HashSet</c> enumeration order, or the graph reshuffles on every save. Document order is the
/// tie-breaker everywhere.
/// </para>
/// </summary>
public static class PipelineLayout
{
    // Geometry. A 200px node plus a 96px gutter reads as a comfortable pipeline at 100% zoom.
    public const double NodeWidth = 200;
    public const double NodeHeight = 64;
    public const double PortRowHeight = 18;
    public const double HGap = 96;
    public const double VGap = 28;
    public const double BackChannelGap = 24;

    /// <summary>
    /// Lays out a graph. Pass the <paramref name="previous"/> layout so sibling ordering is seeded from it —
    /// without that, adding one step reshuffles the whole graph and the canvas feels violent.
    /// </summary>
    public static GraphLayout Compute(PipelineGraph? graph, GraphLayout? previous = null, int revision = 0)
    {
        if (graph is null || graph.Nodes.Count == 0)
            return GraphLayout.Empty with { Revision = revision };

        // ---- 0. normalize: keep only well-formed nodes, dedupe edges, drop dangling ones ----
        var nodes = new List<PipelineNodeDef>();
        var byId = new Dictionary<string, PipelineNodeDef>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id)) continue;
            if (!byId.TryAdd(node.Id, node)) continue;
            nodes.Add(node);
        }
        if (nodes.Count == 0) return GraphLayout.Empty with { Revision = revision };

        var documentIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++) documentIndex[nodes[i].Id] = i;

        var edges = new List<PipelineEdgeDef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selfLoops = new List<string>();

        foreach (var edge in graph.Edges)
        {
            if (edge.From is null || edge.To is null) continue;
            if (!byId.ContainsKey(edge.From) || !byId.ContainsKey(edge.To)) continue;

            // A self-loop renders as a node badge, not an arc — an arc adds nothing and is fiddly to place.
            if (edge.From == edge.To) { selfLoops.Add(edge.From); continue; }

            var fingerprint = $"{edge.From}␟{edge.FromPort}␟{edge.To}␟{edge.ToPort}";
            if (!seen.Add(fingerprint)) continue;
            edges.Add(edge);
        }

        // ---- 1. cycle breaking: three-colour DFS, back edges excluded from layering ----
        // The compiler rejects cycles, but the editor lays out invalid graphs mid-edit, so this must cope.
        var backEdges = FindBackEdges(nodes, edges, documentIndex);
        var acyclic = edges.Where(e => !backEdges.Contains(EdgeKey(e))).ToList();

        // ---- 2. layering: longest path from sources ----
        var layer = AssignLayers(nodes, acyclic, documentIndex);

        // Sources always read as the leftmost column, whatever their in-degree says. (Relay pinned its
        // single trigger here; a pipeline can have several sources, and they all belong in column zero.)
        foreach (var node in nodes)
            if (PipelineNodeCatalog.Get(node.Type)?.IsSource == true) layer[node.Id] = 0;

        var maxLayer = layer.Count == 0 ? 0 : layer.Values.Max();

        // ---- 3. slots, plus dummy nodes for edges spanning more than one layer ----
        // Dummies keep a skip connection from drawing straight through the middle of everything, and they
        // reserve the vertical channels an orthogonal edge mode would later need.
        var layers = new List<List<Slot>>();
        for (var i = 0; i <= maxLayer; i++) layers.Add([]);

        var slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var slot = new Slot
            {
                Id = node.Id,
                Layer = layer[node.Id],
                Width = NodeWidth,
                Height = HeightOf(node)
            };
            slots[node.Id] = slot;
            layers[slot.Layer].Add(slot);
        }

        var chains = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var dummyLinks = new List<(string From, string To)>();

        foreach (var edge in acyclic)
        {
            var from = layer[edge.From!];
            var to = layer[edge.To!];
            var chain = new List<string> { edge.From! };

            if (to - from > 1)
            {
                for (var l = from + 1; l < to; l++)
                {
                    var dummyId = $"d:{EdgeKey(edge)}:{l}";
                    var dummy = new Slot { Id = dummyId, Layer = l, Width = 1, Height = 1, IsDummy = true };
                    slots[dummyId] = dummy;
                    layers[l].Add(dummy);
                    chain.Add(dummyId);
                }
            }

            chain.Add(edge.To!);
            chains[EdgeKey(edge)] = chain;

            for (var i = 0; i < chain.Count - 1; i++)
                dummyLinks.Add((chain[i], chain[i + 1]));
        }

        // ---- 4. ordering: median heuristic, 4 alternating sweeps, seeded from the previous layout ----
        var predecessors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var successors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in slots.Keys) { predecessors[id] = []; successors[id] = []; }
        foreach (var (from, to) in dummyLinks) { successors[from].Add(to); predecessors[to].Add(from); }

        SeedOrder(layers, previous, documentIndex);

        for (var iteration = 0; iteration < 4; iteration++)
        {
            if (iteration % 2 == 0)
            {
                for (var l = 1; l <= maxLayer; l++) SortByMedian(layers[l], layers[l - 1], predecessors);
            }
            else
            {
                for (var l = maxLayer - 1; l >= 0; l--) SortByMedian(layers[l], layers[l + 1], successors);
            }
        }

        for (var l = 0; l <= maxLayer; l++)
            for (var i = 0; i < layers[l].Count; i++) layers[l][i].Order = i;

        // ---- 5. coordinates ----
        AssignX(layers);
        AssignY(layers, predecessors, successors, maxLayer);

        // ---- 6. apply manual pins (the hybrid canvas: auto-placed, drag to override) ----
        var pins = graph.Layout?.Pins;
        if (pins is { Count: > 0 })
        {
            foreach (var (id, pin) in pins)
                if (slots.TryGetValue(id, out var slot) && !slot.IsDummy)
                {
                    slot.X = pin.X;
                    slot.Y = pin.Y;
                    slot.Pinned = true;
                }
        }

        // ---- 7. edge routing ----
        var boxes = new List<NodeBox>();
        foreach (var node in nodes)
        {
            var slot = slots[node.Id];
            boxes.Add(new NodeBox(node.Id, slot.X, slot.Y, slot.Width, slot.Height,
                slot.Layer, slot.Order, slot.Pinned));
        }

        var minX = boxes.Min(b => b.X);
        var minY = boxes.Min(b => b.Y);
        var maxX = boxes.Max(b => b.X + b.Width);
        var maxY = boxes.Max(b => b.Y + b.Height);

        var backEdgeList = backEdges.ToList();
        var routed = new List<EdgePath>();

        foreach (var edge in edges)
        {
            var key = EdgeKey(edge);
            var isBack = backEdges.Contains(key);

            var d = isBack
                ? BackEdgePath(slots, edge, maxY, backEdgeList.IndexOf(key))
                : ForwardEdgePath(slots, byId, chains.GetValueOrDefault(key), edge);

            if (d is null) continue;

            var mid = Midpoint(slots, byId, edge);

            routed.Add(new EdgePath(
                key, edge.From!, edge.To!, edge.FromPort, edge.ToPort, d,
                isBack ? EdgeShape.Back : EdgeShape.Forward, edge.Label, mid.X, mid.Y));
        }

        // NOTE: relay computes a second "data lineage" edge set here, derived from which nodes read which
        // others' output through {{ }} bindings. There is deliberately no equivalent, and the reason is
        // structural rather than a shortcut: a workflow separates control flow from data flow, so the two
        // edge sets genuinely differ and showing both is informative. A pipeline's edges ARE its data flow —
        // an edge exists precisely because one step reads another's rows. A lineage overlay would be an
        // exact copy of the control-flow edges, so it would tell the reader nothing.

        return new GraphLayout(
            boxes, routed,
            backEdgeList, selfLoops.Distinct(StringComparer.Ordinal).ToList(),
            minX, minY, maxX, maxY, revision);
    }

    // ---------------- sizing ----------------

    /// <summary>
    /// Tall enough for whichever side has more ports. Relay only had to consider outputs, because its nodes
    /// had one input; a join has two inputs and one output, so both sides matter here.
    /// </summary>
    private static double HeightOf(PipelineNodeDef node)
    {
        var spec = PipelineNodeCatalog.Get(node.Type);
        if (spec is null) return NodeHeight;

        var ports = Math.Max(spec.InPorts.Count, spec.OutPorts.Count);
        return ports > 1 ? NodeHeight + PortRowHeight * (ports - 1) : NodeHeight;
    }

    /// <summary>Output-port anchor, spread evenly down the node's right edge.</summary>
    public static (double X, double Y) OutPortAnchor(NodeBox box, int portIndex, int portCount) =>
        portCount <= 1
            ? (box.X + box.Width, box.Y + box.Height / 2)
            : (box.X + box.Width, box.Y + box.Height * (portIndex + 1) / (portCount + 1));

    /// <summary>
    /// Input-port anchor, spread evenly down the node's left edge. Relay's equivalent was always the
    /// mid-left point because its nodes had a single input; a join's left and right inputs have to be
    /// visually distinguishable or the canvas cannot show which side is which.
    /// </summary>
    public static (double X, double Y) InPortAnchor(NodeBox box, int portIndex, int portCount) =>
        portCount <= 1
            ? (box.X, box.Y + box.Height / 2)
            : (box.X, box.Y + box.Height * (portIndex + 1) / (portCount + 1));

    // ---------------- 1. cycle breaking ----------------

    private static HashSet<string> FindBackEdges(
        List<PipelineNodeDef> nodes, List<PipelineEdgeDef> edges, Dictionary<string, int> documentIndex)
    {
        var outgoing = new Dictionary<string, List<PipelineEdgeDef>>(StringComparer.Ordinal);
        foreach (var node in nodes) outgoing[node.Id] = [];
        foreach (var edge in edges) outgoing[edge.From!].Add(edge);

        // Deterministic traversal: iterate roots and successors in document order.
        foreach (var list in outgoing.Values)
            list.Sort((a, b) => documentIndex[a.To!].CompareTo(documentIndex[b.To!]));

        const int white = 0, grey = 1, black = 2;
        var colour = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes) colour[node.Id] = white;

        var back = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string id)
        {
            colour[id] = grey;
            foreach (var edge in outgoing[id])
            {
                // An edge reaching a grey node closes a cycle, so it is the back edge.
                if (colour[edge.To!] == grey) back.Add(EdgeKey(edge));
                else if (colour[edge.To!] == white) Visit(edge.To!);
            }
            colour[id] = black;
        }

        foreach (var node in nodes)
            if (colour[node.Id] == white) Visit(node.Id);

        return back;
    }

    // ---------------- 2. layering ----------------

    private static Dictionary<string, int> AssignLayers(
        List<PipelineNodeDef> nodes, List<PipelineEdgeDef> edges, Dictionary<string, int> documentIndex)
    {
        var layer = new Dictionary<string, int>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var successors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var node in nodes) { layer[node.Id] = 0; inDegree[node.Id] = 0; successors[node.Id] = []; }
        foreach (var edge in edges) { successors[edge.From!].Add(edge.To!); inDegree[edge.To!]++; }

        var queue = new Queue<string>(nodes.Where(n => inDegree[n.Id] == 0).Select(n => n.Id));
        var processed = 0;

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            processed++;
            foreach (var next in successors[id])
            {
                layer[next] = Math.Max(layer[next], layer[id] + 1);
                if (--inDegree[next] == 0) queue.Enqueue(next);
            }
        }

        // Residual nodes belong to a cycle the DFS did not fully break (a graph can be mid-edit and
        // pathological). Place them after their deepest resolved predecessor so they render somewhere
        // sensible rather than all piling onto layer 0.
        if (processed < nodes.Count)
        {
            foreach (var node in nodes.OrderBy(n => documentIndex[n.Id]))
            {
                if (inDegree[node.Id] == 0) continue;
                var deepest = edges.Where(e => e.To == node.Id)
                    .Select(e => layer.GetValueOrDefault(e.From!, 0))
                    .DefaultIfEmpty(0).Max();
                layer[node.Id] = Math.Max(layer[node.Id], deepest + 1);
            }
        }

        return layer;
    }

    // ---------------- 4. ordering ----------------

    /// <summary>
    /// Seeds each layer's order from the previous layout so a small edit produces a small visual change.
    /// Nodes the previous layout did not know about sort last, in document order.
    /// </summary>
    private static void SeedOrder(
        List<List<Slot>> layers, GraphLayout? previous, Dictionary<string, int> documentIndex)
    {
        var previousOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        if (previous is not null)
            foreach (var box in previous.Nodes) previousOrder[box.Id] = box.Order;

        foreach (var slots in layers)
        {
            slots.Sort((a, b) =>
            {
                var aKnown = previousOrder.TryGetValue(a.Id, out var ao);
                var bKnown = previousOrder.TryGetValue(b.Id, out var bo);

                if (aKnown && bKnown && ao != bo) return ao.CompareTo(bo);
                if (aKnown != bKnown) return aKnown ? -1 : 1;

                var ad = documentIndex.GetValueOrDefault(a.Id, int.MaxValue);
                var bd = documentIndex.GetValueOrDefault(b.Id, int.MaxValue);
                if (ad != bd) return ad.CompareTo(bd);
                return string.CompareOrdinal(a.Id, b.Id);
            });

            for (var i = 0; i < slots.Count; i++) slots[i].Order = i;
        }
    }

    /// <summary>
    /// Median (barycentre) heuristic: order each layer by the median position of its neighbours in the
    /// reference layer. A node with no neighbour there keeps its current index — the classic rule, and what
    /// stops unconnected nodes from drifting.
    /// </summary>
    private static void SortByMedian(
        List<Slot> target, List<Slot> reference, Dictionary<string, List<string>> neighbours)
    {
        var referenceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < reference.Count; i++) referenceIndex[reference[i].Id] = i;

        var keys = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < target.Count; i++)
        {
            var slot = target[i];
            var positions = neighbours[slot.Id]
                .Where(referenceIndex.ContainsKey)
                .Select(n => (double)referenceIndex[n])
                .OrderBy(v => v)
                .ToList();

            keys[slot.Id] = positions.Count switch
            {
                0 => i,
                1 => positions[0],
                _ => positions.Count % 2 == 1
                     ? positions[positions.Count / 2]
                     : (positions[positions.Count / 2 - 1] + positions[positions.Count / 2]) / 2
            };
        }

        // OrderBy is a stable sort in .NET, so equal keys preserve the seeded order.
        var sorted = target.OrderBy(s => keys[s.Id]).ToList();
        target.Clear();
        target.AddRange(sorted);
        for (var i = 0; i < target.Count; i++) target[i].Order = i;
    }

    // ---------------- 5. coordinates ----------------

    private static void AssignX(List<List<Slot>> layers)
    {
        double x = 0;
        foreach (var slots in layers)
        {
            var widest = slots.Count == 0 ? 0 : slots.Max(s => s.Width);
            foreach (var slot in slots) slot.X = x;
            x += widest + HGap;
        }
    }

    private static void AssignY(
        List<List<Slot>> layers,
        Dictionary<string, List<string>> predecessors,
        Dictionary<string, List<string>> successors,
        int maxLayer)
    {
        // Pass 1 — pack each layer top-down with a fixed gutter.
        foreach (var slots in layers) Pack(slots, null);

        // Pass 2 — three alignment sweeps, pulling each node toward its neighbours' centre and re-packing.
        // This is what removes the staircase look and makes the output read as designed rather than homemade.
        for (var sweep = 0; sweep < 3; sweep++)
        {
            for (var l = 1; l <= maxLayer; l++) Align(layers[l], layers[l - 1], predecessors);
            for (var l = maxLayer - 1; l >= 0; l--) Align(layers[l], layers[l + 1], successors);
        }

        // Pass 3 — centre every layer on a common mid-line.
        //
        // DIVERGENCE FROM RELAY, deliberate. Relay centres on the tallest layer's midline
        // (`centres.Max()`), which couples every layer's position to every other layer's height: adding one
        // node anywhere re-centres the whole graph, and the canvas visibly jumps. Measured on a 7-node
        // graph, inserting a single step moved all 7 existing nodes, by two different amounts.
        //
        // Centring on a fixed line instead makes each layer's position depend only on its own contents, so
        // growing one layer leaves the others exactly where they were. The cost is that Y can go negative,
        // which is harmless: coordinates are graph-space, the canvas applies one CSS transform, and MinY is
        // reported for fit-to-view. Normalising back to a zero origin would undo the whole benefit, because
        // it would reintroduce a global shift every time the topmost layer grew upward.
        const double midline = 0;

        foreach (var slots in layers)
        {
            if (slots.Count == 0) continue;
            var centre = (slots.Min(s => s.Y) + slots.Max(s => s.Y + s.Height)) / 2;
            var shift = midline - centre;
            foreach (var slot in slots) slot.Y += shift;
        }
    }

    private static void Pack(List<Slot> slots, Dictionary<string, double>? desired)
    {
        double cursor = 0;
        foreach (var slot in slots)
        {
            var want = desired is not null && desired.TryGetValue(slot.Id, out var d) ? d : cursor;
            slot.Y = Math.Max(want, cursor);
            cursor = slot.Y + slot.Height + VGap;
        }
    }

    private static void Align(
        List<Slot> target, List<Slot> reference, Dictionary<string, List<string>> neighbours)
    {
        if (target.Count == 0 || reference.Count == 0) return;

        var referenceCentre = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var slot in reference) referenceCentre[slot.Id] = slot.Y + slot.Height / 2;

        var desired = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var slot in target)
        {
            var centres = neighbours[slot.Id]
                .Where(referenceCentre.ContainsKey)
                .Select(n => referenceCentre[n])
                .OrderBy(v => v)
                .ToList();

            if (centres.Count == 0) continue;

            var median = centres.Count % 2 == 1
                ? centres[centres.Count / 2]
                : (centres[centres.Count / 2 - 1] + centres[centres.Count / 2]) / 2;

            desired[slot.Id] = median - slot.Height / 2;
        }

        Pack(target, desired);
    }

    // ---------------- 7. routing ----------------

    /// <summary>
    /// Cubic bezier with horizontal control points — the dbt / n8n look. Chosen over orthogonal routing
    /// because node-avoiding orthogonal edges need a channel-assignment pass, which is a large part of why
    /// graph layout is normally a library.
    /// </summary>
    private static string? ForwardEdgePath(
        Dictionary<string, Slot> slots,
        Dictionary<string, PipelineNodeDef> byId,
        List<string>? chain,
        PipelineEdgeDef edge)
    {
        if (!slots.TryGetValue(edge.From!, out var fromSlot) || !slots.TryGetValue(edge.To!, out var toSlot))
            return null;

        var start = AnchorOut(fromSlot, byId, edge);
        var end = AnchorIn(toSlot, byId, edge);

        // Walk the dummy waypoints so a long edge bends through its reserved channel instead of cutting
        // across the intervening layers.
        var waypoints = new List<(double X, double Y)>();
        if (chain is { Count: > 2 })
            for (var i = 1; i < chain.Count - 1; i++)
            {
                var dummy = slots[chain[i]];
                waypoints.Add((dummy.X, dummy.Y));
            }

        var path = new System.Text.StringBuilder();
        path.Append($"M {F(start.X)} {F(start.Y)}");

        var current = start;
        foreach (var point in waypoints)
        {
            path.Append(Curve(current, point));
            current = point;
        }
        path.Append(Curve(current, end));

        return path.ToString();
    }

    /// <summary>
    /// Back edges run through a channel below the content, stacked so several do not overlap, and are dashed
    /// by the stylesheet. A valid pipeline has none — the compiler rejects cycles — but the editor has to
    /// draw an invalid graph mid-edit so the author can see the loop they just made.
    /// </summary>
    private static string? BackEdgePath(
        Dictionary<string, Slot> slots, PipelineEdgeDef edge, double contentBottom, int stackIndex)
    {
        if (!slots.TryGetValue(edge.From!, out var fromSlot) || !slots.TryGetValue(edge.To!, out var toSlot))
            return null;

        var start = (X: fromSlot.X + fromSlot.Width / 2, Y: fromSlot.Y + fromSlot.Height);
        var end = (X: toSlot.X + toSlot.Width / 2, Y: toSlot.Y + toSlot.Height);
        var channelY = contentBottom + BackChannelGap * (Math.Max(0, stackIndex) + 1);

        return $"M {F(start.X)} {F(start.Y)} " +
               $"C {F(start.X)} {F(channelY)}, {F(end.X)} {F(channelY)}, {F(end.X)} {F(end.Y)}";
    }

    private static string Curve((double X, double Y) from, (double X, double Y) to)
    {
        var dx = Math.Clamp((to.X - from.X) * 0.5, 24, 90);
        return $" C {F(from.X + dx)} {F(from.Y)}, {F(to.X - dx)} {F(to.Y)}, {F(to.X)} {F(to.Y)}";
    }

    private static (double X, double Y) AnchorOut(
        Slot slot, Dictionary<string, PipelineNodeDef> byId, PipelineEdgeDef edge)
    {
        if (slot.IsDummy) return (slot.X, slot.Y);

        var ports = byId.TryGetValue(edge.From!, out var node)
            ? PipelineNodeCatalog.OutPortsFor(node)
            : [];
        var index = Math.Max(0, ports.ToList().IndexOf(edge.FromPort ?? PipelinePorts.Out));

        return ports.Count <= 1
            ? (slot.X + slot.Width, slot.Y + slot.Height / 2)
            : (slot.X + slot.Width, slot.Y + slot.Height * (index + 1) / (ports.Count + 1));
    }

    private static (double X, double Y) AnchorIn(
        Slot slot, Dictionary<string, PipelineNodeDef> byId, PipelineEdgeDef edge)
    {
        if (slot.IsDummy) return (slot.X, slot.Y);

        var ports = byId.TryGetValue(edge.To!, out var node)
            ? PipelineNodeCatalog.InPortsFor(node)
            : [];
        var index = Math.Max(0, ports.ToList().IndexOf(edge.ToPort ?? PipelinePorts.In));

        return ports.Count <= 1
            ? (slot.X, slot.Y + slot.Height / 2)
            : (slot.X, slot.Y + slot.Height * (index + 1) / (ports.Count + 1));
    }

    /// <summary>
    /// Where the insert-a-step affordance sits. The chord midpoint, which for a bezier with horizontal
    /// control points is essentially on the curve — close enough for a hit target, and it avoids evaluating
    /// the curve.
    /// </summary>
    private static (double X, double Y) Midpoint(
        Dictionary<string, Slot> slots, Dictionary<string, PipelineNodeDef> byId, PipelineEdgeDef edge)
    {
        if (!slots.TryGetValue(edge.From!, out var fromSlot) || !slots.TryGetValue(edge.To!, out var toSlot))
            return (0, 0);

        var start = AnchorOut(fromSlot, byId, edge);
        var end = AnchorIn(toSlot, byId, edge);

        return ((start.X + end.X) / 2, (start.Y + end.Y) / 2);
    }

    private static string F(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string EdgeKey(PipelineEdgeDef edge) =>
        string.IsNullOrWhiteSpace(edge.Id)
            ? $"{edge.From}␟{edge.FromPort}␟{edge.To}␟{edge.ToPort}"
            : edge.Id;

    /// <summary>Mutable working node, including the dummies that never get rendered.</summary>
    private sealed class Slot
    {
        public string Id = "";
        public bool IsDummy;
        public int Layer;
        public int Order;
        public double X, Y, Width, Height;
        public bool Pinned;
    }
}

public enum EdgeShape { Forward, Back }

/// <summary>A laid-out node. Coordinates are graph-space; the canvas applies pan/zoom via one CSS transform.</summary>
public sealed record NodeBox(
    string Id, double X, double Y, double Width, double Height, int Layer, int Order, bool Pinned);

/// <summary>A routed edge. <c>D</c> is a ready-to-render SVG path, cached until the layout revision changes.</summary>
public sealed record EdgePath(
    string Id, string From, string To, string? FromPort, string? ToPort,
    string D, EdgeShape Shape, string? Label,
    /// <summary>Graph-space midpoint, where the insert-a-step affordance sits.</summary>
    double MidX = 0, double MidY = 0);

/// <summary>
/// The complete geometry for one graph. Because every coordinate is known in C#, box-select, fit-to-view,
/// the minimap, keyboard navigation and popover placement all work with <b>no DOM measurement at all</b>.
/// </summary>
public sealed record GraphLayout(
    IReadOnlyList<NodeBox> Nodes,
    IReadOnlyList<EdgePath> Edges,
    IReadOnlyList<string> BackEdgeIds,
    IReadOnlyList<string> SelfLoopNodeIds,
    double MinX, double MinY, double MaxX, double MaxY,
    int Revision)
{
    public static readonly GraphLayout Empty = new([], [], [], [], 0, 0, 0, 0, 0);

    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    private Dictionary<string, NodeBox>? _index;
    private Dictionary<string, NodeBox> Index =>
        _index ??= Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

    public NodeBox? Box(string id) => Index.GetValueOrDefault(id);

    /// <summary>Nodes whose box intersects a graph-space rectangle — the box-select hit test, done in C#.</summary>
    public IEnumerable<NodeBox> Intersecting(double x1, double y1, double x2, double y2)
    {
        var left = Math.Min(x1, x2);
        var right = Math.Max(x1, x2);
        var top = Math.Min(y1, y2);
        var bottom = Math.Max(y1, y2);

        return Nodes.Where(n =>
            n.X < right && n.X + n.Width > left &&
            n.Y < bottom && n.Y + n.Height > top);
    }
}
