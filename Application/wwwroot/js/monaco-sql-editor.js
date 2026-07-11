// JS-interop wrapper around the Monaco editor for the SQL workbench.
// The Monaco AMD loader (loader.min.js) is loaded globally in App.razor; we lazily configure it
// and load the editor on first use. One editor instance per element id.

let _monacoReady = null;
let _completionRegistered = false;

// Per-editor autocomplete state: elementId -> table/reference names currently offered for that editor.
const completionsByElement = {};
// Per-editor column metadata: elementId -> { tableName: [columnName, ...] }. Populated only for tables we
// actually have schema for (the notebook's own dataset tables) — live external-source tables fall back to
// table-name-only completion since we have no column endpoint for those.
const columnsByElement = {};
// Maps each editor's text model back to its elementId, so the one global 'sql' completion provider
// (Monaco registers providers per LANGUAGE, not per editor instance) can look up the right table list.
const modelToElementId = new Map();

const QUOTED_SAFE = /^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/;
const quoteIfNeeded = (name) => QUOTED_SAFE.test(name) ? name : `"${name}"`;

function registerCompletionProvider(monaco) {
    if (_completionRegistered) return;
    _completionRegistered = true;

    // Adds to (doesn't replace) Monaco's built-in generic SQL keyword completions for this language.
    monaco.languages.registerCompletionItemProvider('sql', {
        triggerCharacters: [' ', '.'],
        provideCompletionItems(model, position) {
            const elementId = modelToElementId.get(model);
            const tables = (elementId && completionsByElement[elementId]) || [];
            const columnMap = (elementId && columnsByElement[elementId]) || {};
            if (tables.length === 0 && Object.keys(columnMap).length === 0) return { suggestions: [] };

            const word = model.getWordUntilPosition(position);
            const range = {
                startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
                startColumn: word.startColumn, endColumn: word.endColumn
            };

            // "alias." or "table." immediately before the cursor — offer that table's columns only.
            const textBeforeWord = model.getValueInRange({
                startLineNumber: position.lineNumber, startColumn: 1,
                endLineNumber: position.lineNumber, endColumn: word.startColumn
            });
            const dotMatch = /([A-Za-z_][A-Za-z0-9_]*)\.\s*$/.exec(textBeforeWord);
            if (dotMatch) {
                const prefix = dotMatch[1];
                const tableKey = Object.keys(columnMap).find(t => t === prefix || t.split('.').pop() === prefix);
                const columns = tableKey ? columnMap[tableKey] : [];
                const suggestions = columns.map(c => ({
                    label: c,
                    kind: monaco.languages.CompletionItemKind.Field,
                    insertText: quoteIfNeeded(c),
                    detail: `column · ${tableKey}`,
                    range
                }));
                return { suggestions };
            }

            // sortText controls ranking when nothing's typed yet (an empty prefix matches everything
            // equally, so Monaco would otherwise fall back to plain alphabetical order by label — that
            // interleaves tables and columns arbitrarily depending on your schema's naming). Columns are
            // the more useful suggestion right after SELECT/WHERE, so they're forced to rank first.
            const tableSuggestions = tables.map(t => ({
                label: t,
                kind: monaco.languages.CompletionItemKind.Struct,
                // Schema-qualified live-source refs (e.g. "dbo.Orders") must stay unquoted — wrapping the
                // whole dotted name in quotes is invalid SQL. Only quote names that aren't plain/dotted identifiers.
                insertText: quoteIfNeeded(t),
                detail: 'table',
                sortText: '1_' + t,
                range
            }));

            // Unqualified position (no leading "table."): also offer every known column across all
            // tables, deduplicated, so typing a column name works without the user having to type the
            // table prefix first — the common case in SELECT/WHERE clauses.
            const seenColumns = new Set();
            const columnSuggestions = [];
            for (const [tableName, cols] of Object.entries(columnMap)) {
                for (const c of cols) {
                    const key = c.toLowerCase();
                    if (seenColumns.has(key)) continue;
                    seenColumns.add(key);
                    columnSuggestions.push({
                        label: c,
                        kind: monaco.languages.CompletionItemKind.Field,
                        insertText: quoteIfNeeded(c),
                        detail: `column · ${tableName}`,
                        sortText: '0_' + c,
                        range
                    });
                }
            }

            return { suggestions: [...tableSuggestions, ...columnSuggestions] };
        }
    });
}

function ensureMonaco() {
    if (_monacoReady) return _monacoReady;
    _monacoReady = new Promise((resolve, reject) => {
        if (window.monaco) { registerCompletionProvider(window.monaco); resolve(window.monaco); return; }
        if (!window.require) { reject(new Error('Monaco loader not available')); return; }
        try {
            window.require.config({
                paths: { vs: 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.40.0/min/vs' }
            });
            window.require(['vs/editor/editor.main'], () => {
                registerCompletionProvider(window.monaco);
                resolve(window.monaco);
            }, reject);
        } catch (e) {
            reject(e);
        }
    });
    return _monacoReady;
}

const editors = {};
const keydownHandlers = {};

export async function init(elementId, dotnetRef, value) {
    const monaco = await ensureMonaco();
    const el = document.getElementById(elementId);
    if (!el) return;

    const editor = monaco.editor.create(el, {
        value: value || '',
        language: 'sql',
        theme: 'vs',
        automaticLayout: true,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        fontSize: 13,
        lineNumbers: 'on',
        tabSize: 2,
    });

    editor.onDidChangeModelContent(() => {
        dotnetRef.invokeMethodAsync('OnValueChanged', editor.getValue());
    });

    // Ctrl+Enter (Cmd+Enter on Mac) — the conventional "run" shortcut. NOT done via editor.addCommand:
    // multiple standalone editors on one page share Monaco's dynamic keybinding service, so each new
    // editor's addCommand call for the SAME keychord silently overwrites the previous editor's binding —
    // Ctrl+Enter would always fire the most-recently-created cell regardless of which one has focus.
    // A plain keydown listener on this editor's OWN container is properly scoped to just this instance.
    const keydownHandler = (e) => {
        if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnRunShortcut');
        }
    };
    el.addEventListener('keydown', keydownHandler);
    keydownHandlers[elementId] = keydownHandler;

    editors[elementId] = editor;
    modelToElementId.set(editor.getModel(), elementId);
}

export function setValue(elementId, value) {
    const editor = editors[elementId];
    if (editor && editor.getValue() !== (value || '')) {
        editor.setValue(value || '');
    }
}

export function getValue(elementId) {
    const editor = editors[elementId];
    return editor ? editor.getValue() : '';
}

// Replaces the table/reference names (and, optionally, per-table column names) offered as autocomplete
// suggestions for this editor instance. `columns` is an object of tableName -> [columnName, ...].
export function setCompletions(elementId, tables, columns) {
    completionsByElement[elementId] = tables || [];
    columnsByElement[elementId] = columns || {};
}

export function dispose(elementId) {
    const editor = editors[elementId];
    if (editor) {
        modelToElementId.delete(editor.getModel());
        editor.dispose();
        delete editors[elementId];
        delete completionsByElement[elementId];
        delete columnsByElement[elementId];
    }
    const handler = keydownHandlers[elementId];
    if (handler) {
        const el = document.getElementById(elementId);
        if (el) el.removeEventListener('keydown', handler);
        delete keydownHandlers[elementId];
    }
}
