// Drag-to-resize for the left nav sidebar. Handled entirely in JS (mousedown/mousemove/mouseup native
// listeners, direct DOM style writes) rather than round-tripping every pixel of movement through Blazor —
// per-event .NET calls during a fast-firing drag caused visible lag in an earlier drag-and-drop feature,
// so this mirrors that lesson. Width is persisted to localStorage; no .NET callback needed at all.
window.sidebarResize = (function () {
    const STORAGE_KEY = 'nav-sidebar-width';
    let initialized = false;

    function init(handleId, sidebarId, minWidth, maxWidth) {
        if (initialized) return;
        const handle = document.getElementById(handleId);
        const sidebar = document.getElementById(sidebarId);
        if (!handle || !sidebar) return;
        initialized = true;

        const saved = parseInt(localStorage.getItem(STORAGE_KEY), 10);
        if (!isNaN(saved)) {
            sidebar.style.width = Math.max(minWidth, Math.min(maxWidth, saved)) + 'px';
        }

        let dragging = false;

        function onMouseDown(e) {
            dragging = true;
            handle.classList.add('nav-resize-active');
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
            e.preventDefault();
        }

        function onMouseMove(e) {
            if (!dragging) return;
            const rect = sidebar.getBoundingClientRect();
            let newWidth = e.clientX - rect.left;
            newWidth = Math.max(minWidth, Math.min(maxWidth, newWidth));
            sidebar.style.width = newWidth + 'px';
        }

        function onMouseUp() {
            if (!dragging) return;
            dragging = false;
            handle.classList.remove('nav-resize-active');
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            localStorage.setItem(STORAGE_KEY, parseInt(sidebar.style.width, 10));
        }

        handle.addEventListener('mousedown', onMouseDown);
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
    }

    return { init };
})();
