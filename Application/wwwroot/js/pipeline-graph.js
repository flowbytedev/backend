// =====================================================================================================
// Pipeline canvas gestures. Ported from relay's `relay.graph`.
//
// THE LOAD-BEARING RULE: there is no Blazor pointer-move handler anywhere in this feature, and pan/zoom
// never re-renders the node tree. This file mutates `.pl-viewport`'s transform inside requestAnimationFrame
// and reports back to .NET only when a gesture ENDS. A two-second pan therefore costs ~120 style writes and
// ONE Blazor render. If you are about to add @onpointermove to the canvas component, don't — put it here.
//
// Instanced by host element id in a Map, so an editor and a read-only run view can coexist on one page.
// =====================================================================================================
window.pipelineGraph = {
    _instances: new Map(),

    init(hostId, dotNetRef, opts) {
        const host = document.getElementById(hostId);
        if (!host) return;
        this.dispose(hostId);

        const o = Object.assign(
            { minZoom: 0.25, maxZoom: 2, lodFar: 0.5, lodMid: 0.75, readOnly: false }, opts || {});

        const viewport = host.querySelector('.pl-viewport');
        if (!viewport) return;

        const inst = {
            host, viewport, dotNet: dotNetRef, opts: o,
            x: 0, y: 0, k: 1,
            frame: 0, mode: null, pointerId: null,
            startX: 0, startY: 0, originX: 0, originY: 0,
            linkPath: null, marquee: null, dragging: null,
            handlers: {}
        };

        const apply = () => {
            inst.frame = 0;
            inst.viewport.style.transform = `translate(${inst.x}px, ${inst.y}px) scale(${inst.k})`;

            // Minimap viewport rectangle. JS owns the transform, so JS moves the rect directly rather than
            // reporting per-frame to .NET — the entire point of not re-rendering during a gesture. The
            // minimap's viewBox is already in graph coordinates, so this needs no measurement of its own.
            const view = inst.viewRect || (inst.viewRect = document.getElementById(hostId + '-view'));
            if (view) {
                const rect = host.getBoundingClientRect();
                view.setAttribute('x', (-inst.x / inst.k).toFixed(2));
                view.setAttribute('y', (-inst.y / inst.k).toFixed(2));
                view.setAttribute('width', (rect.width / inst.k).toFixed(2));
                view.setAttribute('height', (rect.height / inst.k).toFixed(2));
            }
        };
        const schedule = () => { if (!inst.frame) inst.frame = requestAnimationFrame(apply); };
        inst.schedule = schedule;

        // Level of detail: write one class and one custom property, let CSS hide the small stuff. Zero
        // Blazor renders, so zooming out over a large graph costs nothing.
        const setLod = () => {
            host.style.setProperty('--pl-k', String(inst.k));
            host.classList.toggle('is-far', inst.k < o.lodFar);
            host.classList.toggle('is-mid', inst.k >= o.lodFar && inst.k < o.lodMid);
        };
        inst.setLod = setLod;

        // A completed drag still produces a click, which would read as a plain selection and collapse a
        // multi-selection to one node. Swallow exactly that one click, in the capture phase so it never
        // reaches Blazor, and only after movement actually happened.
        const suppressNextClick = () => {
            const swallow = (ev) => {
                ev.stopPropagation();
                ev.preventDefault();
                host.removeEventListener('click', swallow, true);
            };
            host.addEventListener('click', swallow, true);
            // Safety net: some browsers skip the click after a capture, so don't leave the trap armed.
            setTimeout(() => host.removeEventListener('click', swallow, true), 400);
        };

        const toGraph = (clientX, clientY) => {
            const rect = host.getBoundingClientRect();
            return {
                x: (clientX - rect.left - inst.x) / inst.k,
                y: (clientY - rect.top - inst.y) / inst.k
            };
        };
        inst.toGraph = toGraph;

        // Wheel zoom must be a non-passive ELEMENT listener. Blazor delegates events at the document level,
        // where Chrome treats wheel as passive, so @onwheel:preventDefault is silently ignored there.
        const onWheel = (e) => {
            e.preventDefault();
            const rect = host.getBoundingClientRect();
            const px = e.clientX - rect.left, py = e.clientY - rect.top;

            if (e.shiftKey && !e.ctrlKey) {                  // horizontal pan
                inst.x -= e.deltaY;
                schedule();
                return;
            }

            // A trackpad pinch arrives as ctrlKey+wheel. Do not try to tell trackpad from mouse by deltaY
            // magnitude — that is a known dead end.
            const factor = Math.exp(-e.deltaY * 0.0015);
            const next = Math.min(o.maxZoom, Math.max(o.minZoom, inst.k * factor));
            if (next === inst.k) return;

            // Keep the point under the cursor fixed.
            inst.x = px - (px - inst.x) * (next / inst.k);
            inst.y = py - (py - inst.y) * (next / inst.k);
            inst.k = next;
            setLod();
            schedule();
        };

        const onPointerDown = (e) => {
            if (inst.mode) return;
            const onNode = e.target.closest?.('.pl-node');

            // Drag from a port to connect. This is the expected gesture; the click-a-port-then-click-another
            // fallback still works (and is what keyboard and assistive use rely on), so a plain click on a
            // port must fall through to Blazor untouched.
            const onPort = e.target.closest?.('.pl-port');
            if (onPort && e.button === 0 && !o.readOnly) {
                inst.mode = 'linking';
                inst.pointerId = e.pointerId;
                inst.startX = e.clientX; inst.startY = e.clientY;
                inst.moved = false;
                inst.linkOrigin = {
                    node: onPort.dataset.node,
                    port: onPort.dataset.port,
                    dir: onPort.dataset.dir
                };

                // One measurement, at gesture start, to anchor the ghost on the port's centre.
                const r = onPort.getBoundingClientRect();
                const p = toGraph(r.left + r.width / 2, r.top + r.height / 2);
                this.beginLink(hostId, p.x, p.y);
                // Deliberately no preventDefault — see the note at the end of this handler.
                return;
            }
            if (onPort) return;                              // let Blazor handle the click

            const middle = e.button === 1;
            const space = inst.spaceDown;

            if (!onNode && (e.button === 0 || middle)) {
                inst.mode = (e.shiftKey && !middle && !space) ? 'marquee' : 'pan';
            } else if (onNode && (space || middle)) {
                inst.mode = 'pan';
            } else if (onNode && e.button === 0 && !o.readOnly) {
                inst.mode = 'nodedrag';                      // drag-to-pin: the manual-override half
            } else {
                return;
            }

            inst.pointerId = e.pointerId;
            inst.startX = e.clientX; inst.startY = e.clientY;
            inst.originX = inst.x; inst.originY = inst.y;

            if (inst.mode === 'nodedrag') {
                // Drag the whole selection when the grabbed node is part of it, otherwise just this one.
                const grabbed = [onNode];
                if (onNode.classList.contains('is-selected')) {
                    host.querySelectorAll('.pl-node.is-selected').forEach(el => {
                        if (el !== onNode) grabbed.push(el);
                    });
                }
                // Read the start position from the inline style Blazor already wrote — no
                // getBoundingClientRect, and the values are already in graph coordinates.
                inst.dragging = grabbed.map(el => ({
                    el,
                    id: el.id.startsWith('plnode-') ? el.id.slice(7) : null,
                    left: parseFloat(el.style.left) || 0,
                    top: parseFloat(el.style.top) || 0
                })).filter(d => d.id);
                inst.moved = false;
                host.classList.add('is-nodedrag');
            }

            if (inst.mode === 'marquee') {
                const p = toGraph(e.clientX, e.clientY);
                inst.marqueeStart = p;
                inst.marquee = document.createElement('div');
                inst.marquee.className = 'pl-marquee';
                inst.viewport.appendChild(inst.marquee);
            } else if (inst.mode === 'pan') {
                host.classList.add('is-panning');
            }

            // preventDefault ONLY for gestures that have no click target of their own.
            //
            // Calling it on pointerdown also suppresses the compatibility mouse events — including `click` —
            // so doing it over a node or a port stops Blazor's @onclick from ever firing, which silently
            // kills node selection and the click-to-connect fallback while dragging still looks fine. Text
            // selection inside a node is already prevented by `user-select: none`, so nothing is lost.
            if (inst.mode === 'pan' || inst.mode === 'marquee') e.preventDefault();
        };

        const onPointerMove = (e) => {
            if (inst.mode === null) {
                if (inst.linkPath) this._drawLink(inst, e);
                return;
            }
            if (e.pointerId !== inst.pointerId) return;

            if (inst.mode === 'pan') {
                inst.x = inst.originX + (e.clientX - inst.startX);
                inst.y = inst.originY + (e.clientY - inst.startY);
                schedule();
            } else if (inst.mode === 'linking') {
                if (Math.abs(e.clientX - inst.startX) + Math.abs(e.clientY - inst.startY) > 2) inst.moved = true;
                this._drawLink(inst, e);
            } else if (inst.mode === 'nodedrag' && inst.dragging) {
                // Divide by k so a drag tracks the cursor at any zoom.
                const dx = (e.clientX - inst.startX) / inst.k;
                const dy = (e.clientY - inst.startY) / inst.k;
                if (Math.abs(dx) + Math.abs(dy) > 1) inst.moved = true;

                for (const d of inst.dragging) {
                    d.el.style.left = (d.left + dx) + 'px';
                    d.el.style.top = (d.top + dy) + 'px';
                }
            } else if (inst.mode === 'marquee' && inst.marquee) {
                const p = toGraph(e.clientX, e.clientY);
                const s = inst.marqueeStart;
                inst.marquee.style.left = Math.min(s.x, p.x) + 'px';
                inst.marquee.style.top = Math.min(s.y, p.y) + 'px';
                inst.marquee.style.width = Math.abs(p.x - s.x) + 'px';
                inst.marquee.style.height = Math.abs(p.y - s.y) + 'px';
            }
        };

        const finish = (e) => {
            if (!inst.mode) return;
            const mode = inst.mode;
            inst.mode = null;
            inst.pointerId = null;
            host.classList.remove('is-panning');
            host.classList.remove('is-nodedrag');

            if (mode === 'linking') {
                const origin = inst.linkOrigin;
                const moved = inst.moved;
                inst.linkOrigin = null;
                inst.moved = false;
                this.endLink(hostId);

                // A press with no movement is a click: leave it to Blazor's click-then-click path so both
                // interactions stay available.
                if (!moved || !origin) return;

                // A drag already resolved the connection, so the trailing click must not also start a link.
                suppressNextClick();

                // elementFromPoint is the only reliable way to hit-test the drop target, and it runs once.
                const under = document.elementFromPoint(e.clientX, e.clientY);
                const target = under && under.closest ? under.closest('.pl-port') : null;

                if (!target) {
                    // Dropped on empty canvas: offer to create a step there.
                    const p = toGraph(e.clientX, e.clientY);
                    inst.dotNet?.invokeMethodAsync('OnLinkDrop', p.x, p.y);
                    return;
                }

                inst.dotNet?.invokeMethodAsync('OnLinkCompleted',
                    origin.node, origin.port, origin.dir,
                    target.dataset.node, target.dataset.port, target.dataset.dir);
                return;
            }

            if (mode === 'nodedrag') {
                const dragged = inst.dragging || [];
                const moved = inst.moved;
                inst.dragging = null;
                inst.moved = false;

                // A press without movement is a selection: let the click through untouched.
                if (!moved) {
                    inst.dotNet?.invokeMethodAsync('OnNodesMoved', []);
                    return;
                }

                // A real drag: keep the existing selection rather than letting the trailing click reset it.
                suppressNextClick();

                // One call at the end, carrying final graph coordinates read straight back off the style.
                inst.dotNet?.invokeMethodAsync('OnNodesMoved', dragged.map(d => ({
                    id: d.id,
                    x: parseFloat(d.el.style.left) || 0,
                    y: parseFloat(d.el.style.top) || 0
                })));
                return;
            }

            if (mode === 'marquee' && inst.marquee) {
                const p = toGraph(e.clientX, e.clientY);
                const s = inst.marqueeStart;
                inst.marquee.remove();
                inst.marquee = null;
                // One call at the end; C# hit-tests against the boxes it already knows. No DOM reads.
                inst.dotNet?.invokeMethodAsync('OnBoxSelect', s.x, s.y, p.x, p.y);
            } else if (mode === 'pan') {
                const moved = Math.abs(e.clientX - inst.startX) + Math.abs(e.clientY - inst.startY);
                if (moved < 3) {
                    const p = toGraph(e.clientX, e.clientY);
                    inst.dotNet?.invokeMethodAsync('OnCanvasClick', p.x, p.y, e.shiftKey, e.ctrlKey || e.metaKey);
                }
                inst.dotNet?.invokeMethodAsync('OnViewportChanged', inst.x, inst.y, inst.k);
            }
        };

        // An OS-interrupted gesture must reset state, or the canvas stays stuck mid-drag forever.
        const onPointerCancel = () => {
            // Restore any in-flight node drag, or the nodes stay visually detached from their real positions.
            if (inst.dragging) {
                for (const d of inst.dragging) {
                    d.el.style.left = d.left + 'px';
                    d.el.style.top = d.top + 'px';
                }
                inst.dragging = null;
            }
            inst.mode = null; inst.pointerId = null; inst.moved = false;
            inst.linkOrigin = null;
            host.classList.remove('is-panning');
            host.classList.remove('is-nodedrag');
            if (inst.marquee) { inst.marquee.remove(); inst.marquee = null; }
            this.endLink(hostId);
        };

        // Element-scoped keydown, so the canvas owns its shortcuts without touching anything global.
        const onKeyDown = (e) => {
            const t = e.target || {};
            const typing = t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable;

            // Pass the app-wide shortcuts through untouched. "#" opens the global resource search, which is
            // still useful from here; "/" is NOT passed through — see below.
            if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) return;
            if (e.key === '#' && !typing) return;

            // Escape is handled here and possibly elsewhere (an open modal, the command palette). No
            // preventDefault, so it still bubbles: every handler is expected to be idempotent and
            // state-guarded, because the order is not guaranteed.
            if (e.key === 'Escape') {
                inst.dotNet?.invokeMethodAsync('OnCanvasCommand', 'escape');
                return;
            }
            if (typing) return;

            const ctrl = e.ctrlKey || e.metaKey;
            let cmd = null;

            // Shift-modified combos MUST be tested before their unshifted twins, or Ctrl+Shift+A is
            // swallowed by Ctrl+A and Ctrl+Shift+Z by Ctrl+Z.
            if (ctrl && (e.key === 's' || e.key === 'S')) cmd = 'save';
            else if (ctrl && e.shiftKey && (e.key === 'z' || e.key === 'Z')) cmd = 'redo';
            else if (ctrl && e.shiftKey && (e.key === 'a' || e.key === 'A')) cmd = 'addNode';
            else if (ctrl && (e.key === 'y' || e.key === 'Y')) cmd = 'redo';
            else if (ctrl && (e.key === 'z' || e.key === 'Z')) cmd = 'undo';
            else if (ctrl && (e.key === 'a' || e.key === 'A')) cmd = 'selectAll';
            else if (ctrl && (e.key === 'd' || e.key === 'D')) cmd = 'duplicate';
            else if (ctrl && (e.key === 'c' || e.key === 'C')) cmd = 'copy';
            else if (ctrl && (e.key === 'x' || e.key === 'X')) cmd = 'cut';
            else if (ctrl && (e.key === 'v' || e.key === 'V')) cmd = 'paste';
            else if (ctrl && e.key === 'Enter') cmd = 'run';
            else if (e.key === 'Delete' || e.key === 'Backspace') cmd = 'delete';
            else if (e.key === '/') cmd = 'search';
            else if (e.key === '+' || e.key === '=') { this.zoomBy(hostId, 1.2); return; }
            else if (e.key === '-' || e.key === '_') { this.zoomBy(hostId, 1 / 1.2); return; }
            else if (e.key === '0') { this.zoomTo(hostId, 1); return; }
            else if (e.shiftKey && e.key === '!') cmd = 'fit';
            else if (e.key === 'ArrowUp') cmd = 'up';
            else if (e.key === 'ArrowDown') cmd = 'down';
            else if (e.key === 'ArrowLeft') cmd = 'left';
            else if (e.key === 'ArrowRight') cmd = 'right';
            else if (e.key === 'Enter') cmd = 'open';
            else if (e.key === ' ') { inst.spaceDown = true; e.preventDefault(); return; }

            if (!cmd) return;

            e.preventDefault();                              // browser Save / back / bookmark / page-scroll

            // DIVERGENCE FROM RELAY, forced by this app: commandPalette.js listens for "/" on `document` in
            // the bubble phase to open the global page palette. Inside the canvas "/" means search-in-graph,
            // so the event must not reach that listener — preventDefault alone would not stop it, because it
            // checks the key rather than defaultPrevented. This host listener runs first on the way up, so
            // stopping propagation here is what keeps the two features from firing together.
            if (cmd === 'search') e.stopPropagation();

            inst.dotNet?.invokeMethodAsync('OnCanvasCommand', cmd);
        };

        const onKeyUp = (e) => { if (e.key === ' ') inst.spaceDown = false; };

        host.addEventListener('wheel', onWheel, { passive: false });
        host.addEventListener('pointerdown', onPointerDown);

        // Move/up/cancel live on `window`, and setPointerCapture is deliberately NOT used.
        //
        // Pointer capture retargets pointerup to the capturing element, and the browser then dispatches the
        // compatibility `click` at the common ancestor of the down and up targets — i.e. the canvas, not the
        // node. That silently destroys Blazor's @onclick on nodes and ports, so selection and
        // click-to-connect stop working while JS-driven dragging still appears to work fine.
        //
        // Window listeners give the same benefit capture was there for (a drag that wanders outside the host
        // still completes) without touching click targeting. Every handler bails immediately when this
        // instance has no active gesture, so several canvases can coexist.
        window.addEventListener('pointermove', onPointerMove);
        window.addEventListener('pointerup', finish);
        window.addEventListener('pointercancel', onPointerCancel);

        host.addEventListener('keydown', onKeyDown);
        host.addEventListener('keyup', onKeyUp);
        host.addEventListener('contextmenu', (e) => e.preventDefault());

        inst.handlers = { onWheel, onPointerDown, onPointerMove, finish, onPointerCancel, onKeyDown, onKeyUp };
        this._instances.set(hostId, inst);
        setLod();
        apply();
    },

    dispose(hostId) {
        const inst = this._instances.get(hostId);
        if (!inst) return;
        const h = inst.handlers;
        inst.host.removeEventListener('wheel', h.onWheel);
        inst.host.removeEventListener('pointerdown', h.onPointerDown);
        window.removeEventListener('pointermove', h.onPointerMove);
        window.removeEventListener('pointerup', h.finish);
        window.removeEventListener('pointercancel', h.onPointerCancel);
        inst.host.removeEventListener('keydown', h.onKeyDown);
        inst.host.removeEventListener('keyup', h.onKeyUp);
        if (inst.frame) cancelAnimationFrame(inst.frame);
        this._instances.delete(hostId);
    },

    setTransform(hostId, x, y, k) {
        const inst = this._instances.get(hostId);
        if (!inst) return;
        inst.x = x; inst.y = y; inst.k = k;
        inst.setLod(); inst.schedule();
    },

    getTransform(hostId) {
        const inst = this._instances.get(hostId);
        return inst ? { x: inst.x, y: inst.y, k: inst.k } : null;
    },

    zoomBy(hostId, factor) {
        const inst = this._instances.get(hostId);
        if (!inst) return;
        const rect = inst.host.getBoundingClientRect();
        const cx = rect.width / 2, cy = rect.height / 2;
        const next = Math.min(inst.opts.maxZoom, Math.max(inst.opts.minZoom, inst.k * factor));
        if (next === inst.k) return;
        inst.x = cx - (cx - inst.x) * (next / inst.k);
        inst.y = cy - (cy - inst.y) * (next / inst.k);
        inst.k = next;
        inst.setLod(); inst.schedule();
        inst.dotNet?.invokeMethodAsync('OnViewportChanged', inst.x, inst.y, inst.k);
    },

    zoomTo(hostId, k) {
        const inst = this._instances.get(hostId);
        if (!inst) return;
        this.zoomBy(hostId, k / inst.k);
    },

    // C# passes the layout bbox (it knows every coordinate); JS knows the viewport rect. Between them there
    // is no DOM measurement of any node.
    fit(hostId, minX, minY, maxX, maxY, padding) {
        const inst = this._instances.get(hostId);
        if (!inst) return;
        const rect = inst.host.getBoundingClientRect();
        const pad = padding == null ? 48 : padding;
        const w = Math.max(1, maxX - minX), h = Math.max(1, maxY - minY);

        const k = Math.min(
            inst.opts.maxZoom,
            Math.max(inst.opts.minZoom,
                Math.min((rect.width - pad * 2) / w, (rect.height - pad * 2) / h)));

        inst.k = k;
        inst.x = (rect.width - w * k) / 2 - minX * k;
        inst.y = (rect.height - h * k) / 2 - minY * k;
        inst.setLod(); inst.schedule();
        inst.dotNet?.invokeMethodAsync('OnViewportChanged', inst.x, inst.y, inst.k);
    },

    centerOn(hostId, x, y, k) {
        const inst = this._instances.get(hostId);
        if (!inst) return;
        const rect = inst.host.getBoundingClientRect();
        if (k) inst.k = Math.min(inst.opts.maxZoom, Math.max(inst.opts.minZoom, k));
        inst.x = rect.width / 2 - x * inst.k;
        inst.y = rect.height / 2 - y * inst.k;
        inst.setLod(); inst.schedule();
    },

    screenToGraph(hostId, clientX, clientY) {
        const inst = this._instances.get(hostId);
        return inst ? inst.toGraph(clientX, clientY) : null;
    },

    focus(hostId) {
        document.getElementById(hostId)?.focus();
    },

    // Rubber-band while connecting two ports. JS owns the path element outright, so dragging a link costs
    // zero Blazor renders.
    beginLink(hostId, fromX, fromY) {
        const inst = this._instances.get(hostId);
        if (!inst) return;
        const svg = inst.host.querySelector('.pl-edges');
        if (!svg) return;
        this.endLink(hostId);

        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('class', 'pl-linkghost');
        svg.appendChild(path);
        inst.linkPath = path;
        inst.linkFrom = { x: fromX, y: fromY };
        inst.host.classList.add('is-linking');
        this._drawLink(inst, null);
    },

    endLink(hostId) {
        const inst = this._instances.get(hostId);
        if (!inst) return;
        if (inst.linkPath) { inst.linkPath.remove(); inst.linkPath = null; }
        inst.linkFrom = null;
        inst.host.classList.remove('is-linking');
    },

    _drawLink(inst, e) {
        if (!inst.linkPath || !inst.linkFrom) return;
        const from = inst.linkFrom;
        const to = e ? inst.toGraph(e.clientX, e.clientY) : from;
        const dx = Math.max(24, Math.min(90, Math.abs(to.x - from.x) * 0.5));
        inst.linkPath.setAttribute('d',
            `M ${from.x} ${from.y} C ${from.x + dx} ${from.y}, ${to.x - dx} ${to.y}, ${to.x} ${to.y}`);
    }
};

// =====================================================================================================
// Panel resizing. Writes a CSS custom property during the drag, so dragging the palette or inspector edge
// costs ZERO Blazor renders — the same reason the canvas transform is done in JS.
// =====================================================================================================
window.pipelineResize = {
    _handles: new Map(),

    // axis: "x" grows rightward (a left panel), "x-reverse" grows leftward (a right panel), "y-reverse"
    // grows upward (the bottom test panel).
    init(handleId, targetId, axis, cssVar, min, max, storageKey) {
        const handle = document.getElementById(handleId);
        const target = document.getElementById(targetId);
        if (!handle || !target) return;
        this.dispose(handleId);

        if (storageKey) {
            const saved = parseFloat(localStorage.getItem(storageKey) || '');
            if (!isNaN(saved)) target.style.setProperty(cssVar, saved + 'px');
        }

        let startPos = 0, startSize = 0, dragging = false;

        const current = () => {
            const raw = getComputedStyle(target).getPropertyValue(cssVar).trim();
            const parsed = parseFloat(raw);
            return isNaN(parsed) ? min : parsed;
        };

        const onDown = (e) => {
            dragging = true;
            startPos = axis === 'y-reverse' ? e.clientY : e.clientX;
            startSize = current();
            handle.classList.add('is-dragging');
            // Stops the browser from starting a text selection across the whole page mid-drag.
            e.preventDefault();
        };

        const onMove = (e) => {
            if (!dragging) return;
            const pos = axis === 'y-reverse' ? e.clientY : e.clientX;
            const delta = axis === 'x' ? pos - startPos : startPos - pos;
            const next = Math.min(max, Math.max(min, startSize + delta));
            target.style.setProperty(cssVar, next + 'px');
        };

        const onUp = () => {
            if (!dragging) return;
            dragging = false;
            handle.classList.remove('is-dragging');
            if (storageKey) localStorage.setItem(storageKey, String(current()));
        };

        handle.addEventListener('pointerdown', onDown);
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', onUp);
        window.addEventListener('pointercancel', onUp);

        this._handles.set(handleId, { handle, onDown, onMove, onUp });
    },

    dispose(handleId) {
        const h = this._handles.get(handleId);
        if (!h) return;
        h.handle.removeEventListener('pointerdown', h.onDown);
        window.removeEventListener('pointermove', h.onMove);
        window.removeEventListener('pointerup', h.onUp);
        window.removeEventListener('pointercancel', h.onUp);
        this._handles.delete(handleId);
    }
};

// Small localStorage helper, so viewport and panel state survive a reload without a round trip.
window.pipelineStorage = {
    get(key) { try { return localStorage.getItem(key); } catch { return null; } },
    set(key, value) { try { localStorage.setItem(key, value); } catch { /* private mode */ } }
};

// =====================================================================================================
// Editor chrome. The pipeline editor replaces the app's nav with its own step palette, which means
// reaching outside the component's own DOM — the nav lives in MainLayout, a different render tree.
// A body class is the least invasive way to do that: no shared state container, no cascading parameter,
// and the layout keeps working untouched on every other page.
//
// Paired with IAsyncDisposable on the editor page. If the class ever sticks around after navigating
// away, the missing half is that dispose call.
// =====================================================================================================
window.pipelineChrome = {
    focus(on) {
        document.body.classList.toggle('pl-focus', !!on);
    }
};
