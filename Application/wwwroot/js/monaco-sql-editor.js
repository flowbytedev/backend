// JS-interop wrapper around the Monaco editor for the SQL workbench.
// The Monaco AMD loader (loader.min.js) is loaded globally in App.razor; we lazily configure it
// and load the editor on first use. One editor instance per element id.

let _monacoReady = null;
let _completionRegistered = false;

// Per-editor autocomplete state: elementId -> table/reference names currently offered for that editor.
const completionsByElement = {};
// Maps each editor's text model back to its elementId, so the one global 'sql' completion provider
// (Monaco registers providers per LANGUAGE, not per editor instance) can look up the right table list.
const modelToElementId = new Map();

function registerCompletionProvider(monaco) {
    if (_completionRegistered) return;
    _completionRegistered = true;

    // Adds to (doesn't replace) Monaco's built-in generic SQL keyword completions for this language.
    monaco.languages.registerCompletionItemProvider('sql', {
        triggerCharacters: [' ', '.'],
        provideCompletionItems(model, position) {
            const elementId = modelToElementId.get(model);
            const tables = (elementId && completionsByElement[elementId]) || [];
            if (tables.length === 0) return { suggestions: [] };

            const word = model.getWordUntilPosition(position);
            const range = {
                startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
                startColumn: word.startColumn, endColumn: word.endColumn
            };
            const suggestions = tables.map(t => ({
                label: t,
                kind: monaco.languages.CompletionItemKind.Struct,
                // Schema-qualified live-source refs (e.g. "dbo.Orders") must stay unquoted — wrapping the
                // whole dotted name in quotes is invalid SQL. Only quote names that aren't plain/dotted identifiers.
                insertText: /^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(t) ? t : `"${t}"`,
                detail: 'table',
                range
            }));
            return { suggestions };
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

// Replaces the table/reference names offered as autocomplete suggestions for this editor instance.
export function setCompletions(elementId, tables) {
    completionsByElement[elementId] = tables || [];
}

export function dispose(elementId) {
    const editor = editors[elementId];
    if (editor) {
        modelToElementId.delete(editor.getModel());
        editor.dispose();
        delete editors[elementId];
        delete completionsByElement[elementId];
    }
    const handler = keydownHandlers[elementId];
    if (handler) {
        const el = document.getElementById(elementId);
        if (el) el.removeEventListener('keydown', handler);
        delete keydownHandlers[elementId];
    }
}
