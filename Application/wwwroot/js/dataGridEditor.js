// AG Grid (Community) interop for dataset grids.
//
// Two modes:
//  • client  (Query Workbench results): rows are pushed in from .NET; read-only; client-side sort/filter.
//  • infinite (/data/view): AG Grid pulls pages from .NET on demand (server-side paging) so tables with
//    millions of rows scroll smoothly. When editable, edits auto-commit per row (the whole sheet isn't
//    in memory, so there's no bulk save): editing a cell saves that row, deleting removes it immediately.
//
// Excel-like editing on loaded rows: click to focus, type / F2 / Enter to edit, arrow keys to navigate.
//
// Presentation: columns are typed from their declared data type (numbers right-aligned, a small type tag
// beside the header text, a muted "null" placeholder for empty cells) and the grid is themed through the
// `.dvw-grid` custom-property overrides in ViewData.razor — the look lives in CSS, not here.

window.dataGridEditor = (function () {
    let api = null;
    let dotNetRef = null;
    let columns = [];        // [{ name, dataType }]
    let editable = false;
    let infinite = false;
    let sortable = true;
    let filterable = true;
    let autoSized = false;

    const colFields = () => columns.map(c => c.name);
    const norm = v => (v === null || v === undefined || v === "") ? null : String(v);
    const escapeHtml = s => String(s).replace(/[&<>"']/g, ch =>
        ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[ch]));

    // ---- column typing ----
    // Coarse kind from the declared type. DuckDB, SQL Server and PostgreSQL spellings all land here
    // (INTEGER / int / bigint, DECIMAL(18,2) / numeric, TIMESTAMP / datetime2, BOOLEAN / bit …).
    function typeKind(dataType) {
        const t = String(dataType || "").trim().toUpperCase();
        if (!t) return "text";
        if (/^(TINYINT|SMALLINT|INTEGER|INT|BIGINT|HUGEINT|UTINYINT|USMALLINT|UINTEGER|UBIGINT|UHUGEINT|INT\d+|LONG|SHORT|SERIAL|BIGSERIAL)\b/.test(t)) return "int";
        if (/^(DOUBLE|FLOAT|REAL|DECIMAL|NUMERIC|MONEY|SMALLMONEY|NUMBER)/.test(t)) return "num";
        if (/^(TIMESTAMP|DATETIME|TIME|SMALLDATETIME)/.test(t)) return "time";
        if (/^DATE\b/.test(t)) return "date";
        if (/^(BOOL|BOOLEAN|BIT)\b/.test(t)) return "bool";
        if (/^(JSON|STRUCT|LIST|MAP|ARRAY|UNION)/.test(t) || t.endsWith("[]")) return "json";
        if (/^(BLOB|BYTEA|BINARY|VARBINARY|IMAGE)/.test(t)) return "blob";
        return "text";
    }
    const isNumeric = kind => kind === "int" || kind === "num";

    // The tag shown beside the header text: the first word of the declared type ("varchar", "decimal",
    // "timestamp"). The full declaration stays in the header tooltip.
    const typeTag = dataType => String(dataType || "").trim().split(/[\s(<\[]/)[0].toLowerCase().slice(0, 12);

    // AG Grid 31.3's stock header template (it uses `ref` attributes and an <ag-sort-indicator> element),
    // verbatim except for the type tag after the column name. Keeping every stock ref means sorting, the
    // filter icon and the column menu behave exactly as they do with the default header.
    function headerTemplate(tag) {
        return '<div class="ag-cell-label-container" role="presentation">' +
            '<span ref="eMenu" class="ag-header-icon ag-header-cell-menu-button" aria-hidden="true"></span>' +
            '<span ref="eFilterButton" class="ag-header-icon ag-header-cell-filter-button" aria-hidden="true"></span>' +
            '<div ref="eLabel" class="ag-header-cell-label" role="presentation">' +
            '<span ref="eText" class="ag-header-cell-text"></span>' +
            (tag ? '<span class="dvw-col-type">' + escapeHtml(tag) + '</span>' : '') +
            '<span ref="eFilter" class="ag-header-icon ag-header-label-icon ag-filter-icon" aria-hidden="true"></span>' +
            '<ag-sort-indicator ref="eSortIndicator"></ag-sort-indicator>' +
            '</div></div>';
    }

    // In infinite mode the server applies the filter (ViewData.MapOperator), and it understands one
    // condition with these operators only — so that is all the filter UI offers there. Client mode
    // (workbench results) filters in the browser and keeps AG Grid's full set.
    function filterParamsFor(kind) {
        if (!infinite) return undefined;
        return isNumeric(kind)
            ? { filterOptions: ["equals", "greaterThan", "lessThan"], maxNumConditions: 1, buttons: ["reset"] }
            : { filterOptions: ["contains", "equals", "startsWith", "endsWith"], maxNumConditions: 1, buttons: ["reset"] };
    }

    function buildColumnDefs() {
        const dataCols = columns.map(c => {
            const kind = typeKind(c.dataType);
            const numeric = isNumeric(kind);
            const def = {
                headerName: c.name,
                field: c.name,
                editable: editable,
                minWidth: numeric ? 96 : 120,
                headerTooltip: c.dataType ? (c.name + " · " + c.dataType) : c.name,
                headerClass: numeric ? ["dvw-head", "ag-right-aligned-header"] : ["dvw-head"],
                headerComponentParams: { template: headerTemplate(typeTag(c.dataType)) },
                cellClass: numeric ? ["dvw-cell", "dvw-cell-" + kind, "ag-right-aligned-cell"] : ["dvw-cell", "dvw-cell-" + kind],
                cellClassRules: { "dvw-null": p => p.value === null || p.value === undefined || p.value === "" },
                sortable: sortable,
                filter: filterable ? (numeric ? "agNumberColumnFilter" : "agTextColumnFilter") : false,
                floatingFilter: filterable
            };
            const fp = filterParamsFor(kind);
            if (fp) def.filterParams = fp;
            return def;
        });
        if (!editable) return dataCols;

        const selectCol = {
            headerName: "", field: "__sel", width: 44, pinned: "left",
            checkboxSelection: true, headerCheckboxSelection: true,
            editable: false, sortable: false, filter: false, resizable: false,
            floatingFilter: false, suppressMovable: true, lockPosition: true,
            headerClass: "dvw-head", cellClass: "dvw-cell-select"
        };
        return [selectCol, ...dataCols];
    }

    // ---- infinite (server-paged) datasource ----
    function makeDataSource() {
        return {
            getRows: async (params) => {
                if (!dotNetRef) { (params.failCallback || params.fail)?.call(params); return; }
                try {
                    const res = await dotNetRef.invokeMethodAsync(
                        "FetchRows",
                        params.startRow,
                        params.endRow,
                        JSON.stringify(params.sortModel || []),
                        JSON.stringify(params.filterModel || {}));
                    const rows = (res && res.rows) || [];
                    const lastRow = (res && typeof res.lastRow === "number") ? res.lastRow : -1;
                    // AG Grid v31 renamed successCallback→success; support both so it works either way.
                    if (typeof params.successCallback === "function") {
                        params.successCallback(rows, lastRow);
                    } else if (typeof params.success === "function") {
                        params.success({ rowData: rows, rowCount: lastRow >= 0 ? lastRow : undefined });
                    }
                } catch (e) {
                    console.error("dataGridEditor: FetchRows failed", e);
                    if (typeof params.failCallback === "function") params.failCallback();
                    else if (typeof params.fail === "function") params.fail();
                }
            }
        };
    }

    async function onCellValueChanged(event) {
        if (!editable || !dotNetRef) return;
        const rowId = event.data ? event.data.__rowid : null;
        if (rowId == null) return;
        await dotNetRef.invokeMethodAsync("UpdateCell", rowId, event.colDef.field, norm(event.newValue));
    }

    // Approximate width of a column's rendered header: the upper-cased name in the header font, the type
    // tag in the mono font, plus room for the sort/filter icons and cell padding. AG Grid's own auto-size
    // does not count the tag, and off-screen headers are not in the DOM (column virtualisation), so this
    // is measured on a canvas rather than read back from the element.
    let measureCtx = null;
    function headerWidth(gridEl, name, dataType) {
        try {
            measureCtx = measureCtx || document.createElement("canvas").getContext("2d");
            if (!measureCtx) return 0;
            const textEl = gridEl.querySelector(".ag-header-cell-text");
            const headerFont = textEl ? getComputedStyle(textEl).fontFamily : "system-ui, sans-serif";
            const monoFont = getComputedStyle(gridEl).getPropertyValue("--font-mono") || "monospace";
            measureCtx.font = "600 11px " + headerFont;
            let w = measureCtx.measureText(String(name).toUpperCase()).width * 1.06; // letter-spacing
            const tag = typeTag(dataType);
            if (tag) {
                measureCtx.font = "500 10px " + monoFont;
                w += 6 + measureCtx.measureText(tag).width;
            }
            return Math.ceil(w + 72); // sort + filter icons, their gaps and the cell padding
        } catch (e) { return 0; }
    }

    // Size columns to their content once the first rows are on screen: wide enough for the header
    // (name + tag) and the visible data, capped so a long-text column cannot run to thousands of pixels.
    // Purely cosmetic: every branch is guarded and failures are ignored.
    function autoSizeOnce(gridEl) {
        if (!api || autoSized) return;
        autoSized = true;
        try {
            api.autoSizeAllColumns(false);
            if (typeof api.getColumns === "function" && typeof api.setColumnWidth === "function") {
                (api.getColumns() || []).forEach(col => {
                    const id = col.getColId ? col.getColId() : null;
                    if (!id || id === "__sel") return;
                    const def = columns.find(c => c.name === id);
                    const wanted = Math.max(col.getActualWidth(), def ? headerWidth(gridEl, def.name, def.dataType) : 0);
                    const target = Math.min(480, wanted);
                    if (target !== col.getActualWidth()) api.setColumnWidth(col, target);
                });
            }
        } catch (e) { /* sizing is cosmetic */ }
    }

    // Tell .NET how many sorts and filters are active so the page can show (and offer to clear) them.
    // Optional: hosts that pass no .NET reference (the workbench) or no such method are simply skipped.
    function notifyState() {
        if (!api || !dotNetRef) return;
        let sorts = 0, filters = 0;
        try {
            sorts = (api.getColumnState() || []).filter(s => s.sort).length;
            filters = Object.keys(api.getFilterModel() || {}).length;
        } catch (e) { return; }
        dotNetRef.invokeMethodAsync("GridStateChanged", sorts, filters).catch(() => { /* no listener */ });
    }

    return {
        init(elementId, ref, cols, rows, opts) {
            dotNetRef = ref;
            columns = cols || [];
            editable = !!(opts && opts.editable);
            infinite = !!(opts && opts.infinite);
            sortable = !(opts && opts.sortable === false);
            filterable = !(opts && opts.filter === false);
            autoSized = false;
            const el = document.getElementById(elementId);
            if (!el || typeof agGrid === "undefined") return;

            const options = {
                columnDefs: buildColumnDefs(),
                // cellDataType:false → plain text editor for every cell (AG Grid's type inference otherwise
                // discards edits on mixed/null DuckDB columns). filter+floatingFilter give the search row.
                defaultColDef: {
                    resizable: true, sortable: sortable, minWidth: 110,
                    cellDataType: false, enableCellChangeFlash: true,
                    filter: filterable, floatingFilter: filterable
                },
                rowSelection: "multiple",
                suppressRowClickSelection: true,
                singleClickEdit: false,
                stopEditingWhenCellsLoseFocus: true,
                enterNavigatesVertically: true,
                enterNavigatesVerticallyAfterEdit: true,
                // Read-only grids let the user select and copy cell text like a document.
                enableCellTextSelection: !editable,
                ensureDomOrder: !editable,
                rowHeight: 34,
                headerHeight: 36,
                floatingFiltersHeight: 34,
                tooltipShowDelay: 400,
                animateRows: false,
                overlayNoRowsTemplate: '<div class="dvw-overlay">No rows to show</div>',
                overlayLoadingTemplate: '<div class="dvw-overlay"><span class="dvw-overlay-spin"></span>Loading rows…</div>',
                onCellValueChanged: onCellValueChanged,
                onSortChanged: notifyState,
                onFilterChanged: notifyState,
                onFirstDataRendered: () => autoSizeOnce(el),
                // rowDataUpdated fires before the new rows paint; defer so the cells exist to measure.
                onRowDataUpdated: () => { if (!infinite) setTimeout(() => autoSizeOnce(el), 0); }
            };

            if (infinite) {
                options.rowModelType = "infinite";
                options.cacheBlockSize = 100;
                options.maxBlocksInCache = 100;
                options.infiniteInitialRowCount = 100;
                options.blockLoadDebounceMillis = 150; // coalesce rapid scroll into fewer server fetches
                options.datasource = makeDataSource();
            } else {
                options.rowData = rows || [];
            }

            api = agGrid.createGrid(el, options);
        },

        // ---- client mode (Query Workbench) ----
        setColumnsAndData(cols, rows) {
            columns = cols || [];
            autoSized = false; // a new result is a new shape — size it again once it has rendered
            if (api) {
                api.setGridOption("columnDefs", buildColumnDefs());
                api.setGridOption("rowData", rows || []);
            }
        },

        exportCsv(fileName) {
            if (api) api.exportDataAsCsv({ fileName: fileName || "export.csv", columnKeys: colFields() });
        },

        // ---- infinite mode (data viewer) ----
        // Re-fetches the visible blocks from the server (after an edit/delete/insert, or manual refresh).
        refresh() {
            if (api) api.refreshInfiniteCache();
        },

        clearSortAndFilters() {
            if (!api) return;
            api.setFilterModel(null);
            api.applyColumnState({ defaultState: { sort: null } });
        },

        async deleteSelected() {
            if (!api || !dotNetRef) return 0;
            const nodes = api.getSelectedNodes();
            const ids = nodes.map(n => n.data && n.data.__rowid).filter(v => v != null);
            if (!ids.length) return 0;
            await dotNetRef.invokeMethodAsync("DeleteRows", ids);
            api.deselectAll();
            api.refreshInfiniteCache();
            return ids.length;
        },

        async addRow() {
            if (!dotNetRef) return false;
            const ok = await dotNetRef.invokeMethodAsync("InsertRow");
            if (ok && api) api.refreshInfiniteCache();
            return ok;
        },

        dispose() {
            if (api) { api.destroy(); api = null; }
            dotNetRef = null;
            columns = [];
            editable = false;
            infinite = false;
            sortable = true;
            filterable = true;
            autoSized = false;
        }
    };
})();
