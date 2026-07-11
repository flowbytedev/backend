// Triggers a browser file download for a same-origin URL via a transient anchor.
// Cookies are sent automatically, so cookie-based auth still applies.
window.downloadFile = function (url) {
    const a = document.createElement('a');
    a.href = url;
    a.style.display = 'none';
    document.body.appendChild(a);
    a.click();
    a.remove();
};

// Scrolls a same-page element into view — used by the notebook dependency graph to jump to a cell card
// when its node is clicked.
window.scrollToElement = function (id) {
    const el = document.getElementById(id);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' });
};

// Triggers a browser download of in-memory text content (no server round trip) — used for exporting a
// notebook cell's result grid to CSV/Excel straight from the rows already loaded client-side.
window.downloadTextFile = function (fileName, mimeType, content) {
    const blob = new Blob([content], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.style.display = 'none';
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
};
