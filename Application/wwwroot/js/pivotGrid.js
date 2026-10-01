// AG Grid (Community) interop for the data viewer's pivot (Components/PivotView.razor).
//
// The pivot is computed on the server (PivotService); this only draws the result: row fields pinned on the
// left, value columns grouped under their column-field headers (AG Grid column groups, which Community
// supports — the Enterprise-only part is the pivoting itself), and the grand total pinned at the bottom.
//
// The result arrives as the raw JSON string rather than a .NET object so a large pivot is parsed once, here,
// instead of being materialised in the WebAssembly heap and re-serialised across the interop boundary.
//
// It also carries the drag-and-drop plumbing the field pane needs but Blazor cannot express: Firefox will
// not start a drag unless dragstart calls dataTransfer.setData, and a drop target must cancel dragover —
// doing that in Blazor would re-render the pane on every mouse move. The drop itself is handled in .NET.

window.pivotGrid = (function () {
    const grids = new Map(); // elementId -> grid api

    const intFormat = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });
    const decFormat = new Intl.NumberFormat(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 2 });

    function formatValue(p) {
        const v = p.value;
        if (v === null || v === undefined) return "";
        if (typeof v === "number") return Number.isInteger(v) ? intFormat.format(v) : decFormat.format(v);
        return String(v);
    }

    // Leaves are emitted in server order, which is already sorted, so equal header prefixes are adjacent:
    // a group is reused only while the next column shares it, exactly how a spreadsheet merges headers.
    function buildValueColumns(result) {
        const labels = result.valueLabels || [];
        const multipleValues = labels.length > 1;
        const roots = [];

        for (const col of result.columns || []) {
            const path = (col.path || []).slice();
            const valueLabel = labels[col.valueIndex] || "";

            // With one value field its name is redundant under every column, so the last column-field label
            // becomes the leaf instead of a one-child group.
            let leafHeader;
            if (multipleValues || path.length === 0) leafHeader = valueLabel;
            else leafHeader = path.pop();

            const leaf = {
                headerName: leafHeader,
                field: col.field,
                headerTooltip: [...(col.path || []), valueLabel].join(" › "),
                type: "rightAligned",
                minWidth: 90,
                valueFormatter: formatValue,
                headerClass: ["pv-head", "ag-right-aligned-header"].concat(col.isTotal ? ["pv-head-total"] : []),
                cellClass: ["pv-cell", "ag-right-aligned-cell"].concat(col.isTotal ? ["pv-cell-total"] : []),
                cellClassRules: { "pv-empty": p => p.value === null || p.value === undefined }
            };

            let siblings = roots;
            for (const segment of path) {
                const last = siblings[siblings.length - 1];
                if (last && last.children && last.headerName === segment) {
                    siblings = last.children;
                } else {
                    const group = {
                        headerName: segment,
                        headerClass: col.isTotal ? ["pv-group", "pv-group-total"] : ["pv-group"],
                        marryChildren: true,
                        children: []
                    };
                    siblings.push(group);
                    siblings = group.children;
                }
            }
            siblings.push(leaf);
        }
        return roots;
    }

    function buildColumnDefs(result) {
        const rowHeaders = result.rowHeaders || [];
        const defs = rowHeaders.map((h, i) => ({
            headerName: h.label,
            field: h.field,
            pinned: "left",
            minWidth: 120,
            headerClass: ["pv-head", "pv-head-row"],
            cellClass: ["pv-rowhead"],
            // Sort by the server's rank of the underlying value, so "Feb" follows "Jan" and 10 follows 9.
            comparator: (a, b, nodeA, nodeB) => {
                const ra = nodeA && nodeA.data ? nodeA.data["__o" + i] : 0;
                const rb = nodeB && nodeB.data ? nodeB.data["__o" + i] : 0;
                return (ra ?? 0) - (rb ?? 0);
            }
        }));

        // No row fields: the single row is the whole table, so give it a label to sit beside.
        if (rowHeaders.length === 0) {
            defs.push({
                headerName: "",
                colId: "__label",
                valueGetter: () => "All rows",
                pinned: "left",
                width: 110,
                sortable: false,
                headerClass: ["pv-head", "pv-head-row"],
                cellClass: ["pv-rowhead"]
            });
        }

        return defs.concat(buildValueColumns(result));
    }

    function autoSize(api) {
        try {
            api.autoSizeAllColumns(false);
            (api.getColumns() || []).forEach(col => {
                const w = col.getActualWidth();
                if (w > 360) api.setColumnWidth(col, 360);
            });
        } catch (e) { /* sizing is cosmetic */ }
    }

    // Highlights the zone under the pointer and the chip a drop would land before. Purely visual.
    function markOver(root, target) {
        root.querySelectorAll(".pv-drop.is-over").forEach(z => { if (!z.contains(target)) z.classList.remove("is-over"); });
        root.querySelectorAll(".pv-chip.is-before").forEach(c => { if (!c.contains(target)) c.classList.remove("is-before"); });
        const zone = target && target.closest ? target.closest(".pv-drop") : null;
        if (zone) zone.classList.add("is-over");
        const chip = target && target.closest ? target.closest(".pv-chip[data-droppable]") : null;
        if (chip) chip.classList.add("is-before");
    }
    function clearMarks(root) {
        root.querySelectorAll(".is-over, .is-before, .is-dragging").forEach(el =>
            el.classList.remove("is-over", "is-before", "is-dragging"));
    }

    return {
        // Draws (or redraws) the pivot. Returns the number of body rows, for the status line.
        render(elementId, json) {
            const el = document.getElementById(elementId);
            if (!el || typeof agGrid === "undefined") return 0;

            let result;
            try { result = typeof json === "string" ? JSON.parse(json) : json; }
            catch (e) { console.error("pivotGrid: bad result", e); return 0; }

            const rows = result.rows || [];
            const pinned = result.grandTotal ? [result.grandTotal] : [];
            const columnDefs = buildColumnDefs(result);

            let api = grids.get(elementId);
            if (api && !el.querySelector(".ag-root-wrapper")) { // the element was re-rendered under us
                try { api.destroy(); } catch (e) { }
                api = null;
                grids.delete(elementId);
            }

            if (!api) {
                api = agGrid.createGrid(el, {
                    columnDefs,
                    rowData: rows,
                    pinnedBottomRowData: pinned,
                    defaultColDef: { resizable: true, sortable: true, filter: false, suppressMovable: true },
                    rowHeight: 34,
                    headerHeight: 36,
                    groupHeaderHeight: 32,
                    animateRows: false,
                    enableCellTextSelection: true,
                    ensureDomOrder: true,
                    suppressDragLeaveHidesColumns: true,
                    getRowClass: p => p.node && p.node.rowPinned ? "pv-grand" : "",
                    overlayNoRowsTemplate: '<div class="dvw-overlay">No rows match this layout</div>',
                    onFirstDataRendered: p => autoSize(p.api)
                });
                grids.set(elementId, api);
            } else {
                api.setGridOption("columnDefs", columnDefs);
                api.setGridOption("rowData", rows);
                api.setGridOption("pinnedBottomRowData", pinned);
                setTimeout(() => autoSize(api), 0);
            }
            return rows.length;
        },

        exportCsv(elementId, fileName) {
            const api = grids.get(elementId);
            if (api) api.exportDataAsCsv({ fileName: fileName || "pivot.csv", allColumns: true });
        },

        dispose(elementId) {
            const api = grids.get(elementId);
            if (api) { try { api.destroy(); } catch (e) { } }
            grids.delete(elementId);
        },

        // Wires the native drag events the field pane needs (see the header). Idempotent per element.
        initDnd(root) {
            if (!root || root.__pvDnd) return;
            root.__pvDnd = true;

            root.addEventListener("dragstart", e => {
                const item = e.target && e.target.closest ? e.target.closest("[draggable=true]") : null;
                if (!item) return;
                try {
                    e.dataTransfer.setData("text/plain", item.getAttribute("data-field") || "field");
                    e.dataTransfer.effectAllowed = "move";
                } catch (err) { }
                item.classList.add("is-dragging");
            });
            root.addEventListener("dragover", e => {
                if (!e.target.closest || !e.target.closest(".pv-drop")) return;
                e.preventDefault();
                try { e.dataTransfer.dropEffect = "move"; } catch (err) { }
                markOver(root, e.target);
            });
            root.addEventListener("dragleave", e => {
                if (!e.relatedTarget || !root.contains(e.relatedTarget)) clearMarks(root);
            });
            root.addEventListener("drop", e => {
                if (e.target.closest && e.target.closest(".pv-drop")) e.preventDefault(); // no navigation to the text
                clearMarks(root);
            });
            root.addEventListener("dragend", () => clearMarks(root));
        },

        // Layout persistence per table. Failures (private mode, quota) just mean nothing is remembered.
        load(key) {
            try { return localStorage.getItem(key); } catch (e) { return null; }
        },
        save(key, value) {
            try { localStorage.setItem(key, value); } catch (e) { }
        }
    };
})();
