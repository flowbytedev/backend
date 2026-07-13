





// print the object in parameter
function printObject(obj) {
    console.log(obj);
}






// ── Theme (light / dark / system) ──────────────────────────────────────
// The choice is owned by <FluentDesignTheme> (persisted as JSON under "theme").
// Here we mirror it onto <html data-theme> so the app's custom CSS follows the
// same setting, and resolve "system" against the OS preference.
window.appTheme = {
    _media: window.matchMedia('(prefers-color-scheme: dark)'),
    _mode: 'system',

    // Reads the mode persisted by FluentDesignTheme; tolerates a legacy plain string.
    getMode() {
        try {
            const raw = localStorage.getItem('theme');
            if (!raw) return 'system';
            let mode;
            try { mode = JSON.parse(raw).mode; } catch { mode = raw; }
            mode = String(mode || 'system').toLowerCase();
            return (mode === 'dark' || mode === 'light') ? mode : 'system';
        } catch { return 'system'; }
    },

    // Applies a mode to <html data-theme>, resolving "system" to the OS preference.
    applyMode(mode) {
        this._mode = String(mode || 'system').toLowerCase();
        const dark = this._mode === 'dark' || (this._mode !== 'light' && this._media.matches);
        document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
    },

    // Re-reads the persisted mode and re-applies it. Called after Blazor enhanced
    // navigation, which morphs the DOM and would otherwise strip data-theme off <html>
    // (the fresh server HTML never carries it), flipping the page back to light.
    reapply() {
        this.applyMode(this.getMode());
    },

    init() {
        this.applyMode(this.getMode());
        // Keep following the OS while the user has chosen "system".
        this._media.addEventListener('change', () => {
            if (this._mode === 'system') this.applyMode('system');
        });
    }
};
window.appTheme.init();

// File download function for Blazor components
function downloadFileFromByteArray(filename, contentType, byteArray) {
    const blob = new Blob([byteArray], { type: contentType });
    const url = window.URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    window.URL.revokeObjectURL(url);
}

