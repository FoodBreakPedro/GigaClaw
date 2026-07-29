let _es = null;
let _ref = null;
let _autoScroll = true;
let _scrollEl = null;
let _observer = null;
let _scrollListening = false;
let _pendingDelta = "";
let _deltaTimer = null;
let _dispatchQueue = Promise.resolve();

function _scrollToBottom() {
    if (_scrollEl) _scrollEl.scrollTop = _scrollEl.scrollHeight;
}

function _onScroll() {
    if (!_scrollEl) return;
    const el = _scrollEl;
    _autoScroll = el.scrollTop + el.clientHeight >= el.scrollHeight - 30;
}

export function initAutoScroll(element) {
    disposeAutoScroll();
    _scrollEl = element;
    _autoScroll = true;
    _scrollListening = false;

    // Use MutationObserver to auto-scroll whenever new children are added
    _observer = new MutationObserver(() => {
        if (_autoScroll) _scrollToBottom();
        // Attach scroll listener after first mutation (content rendered)
        if (!_scrollListening) {
            _scrollListening = true;
            element.addEventListener("scroll", _onScroll);
        }
    });
    _observer.observe(element, { childList: true, subtree: true });

    // Scroll for already-present content: immediate, next frame, and next task
    _scrollToBottom();
    requestAnimationFrame(_scrollToBottom);
    setTimeout(() => {
        _scrollToBottom();
        // Attach scroll listener if observer hasn't already
        if (!_scrollListening && _scrollEl === element) {
            _scrollListening = true;
            element.addEventListener("scroll", _onScroll);
        }
    }, 0);
}

export function scrollIfNeeded() {
    // Handled by MutationObserver now, but keep as fallback
    if (_autoScroll && _scrollEl) {
        requestAnimationFrame(_scrollToBottom);
    }
}

export function disposeAutoScroll() {
    if (_observer) { _observer.disconnect(); _observer = null; }
    if (_scrollEl && _scrollListening) _scrollEl.removeEventListener("scroll", _onScroll);
    _scrollEl = null;
    _autoScroll = true;
    _scrollListening = false;
}

export function start(dotnetRef, url) {
    stop();
    _ref = dotnetRef;
    _es = new EventSource(url);
    _es.onmessage = (ev) => {
        if (!_ref) return;
        try {
            const data = JSON.parse(ev.data);
            const kind = data.kind ?? "event";
            const text = data.text ?? "";
            if (kind === "content_block_delta") {
                const prefix = "[content_block_delta] ";
                _pendingDelta += text.startsWith(prefix) ? text.slice(prefix.length) : text;
                if (!_deltaTimer) {
                    _deltaTimer = setTimeout(() => {
                        _deltaTimer = null;
                        _flushPendingDelta();
                    }, 60);
                }
                return;
            }

            _flushPendingDelta();
            _enqueueSse(kind, text, data.detail ?? null);
        } catch {
            _flushPendingDelta();
            _enqueueSse("raw", ev.data, null);
        }
    };
    _es.addEventListener("end", () => {
        const target = _ref;
        _flushPendingDelta();
        _dispatchQueue = _dispatchQueue
            .then(() => target ? target.invokeMethodAsync("StreamEnded") : undefined)
            .finally(stop);
    });
    _es.onerror = () => {
        const target = _ref;
        _flushPendingDelta();
        _dispatchQueue = _dispatchQueue
            .then(() => target ? target.invokeMethodAsync("StreamEnded") : undefined)
            .finally(stop);
    };
}

export function stop() {
    if (_es) { try { _es.close(); } catch {} _es = null; }
    if (_deltaTimer) { clearTimeout(_deltaTimer); _deltaTimer = null; }
    _pendingDelta = "";
    _ref = null;
}

function _enqueueSse(kind, text, detail) {
    const target = _ref;
    _dispatchQueue = _dispatchQueue
        .then(() => target ? target.invokeMethodAsync("ReceiveSse", kind, text, detail) : undefined)
        .catch(() => undefined);
}

function _flushPendingDelta() {
    if (!_pendingDelta) return;
    if (_deltaTimer) { clearTimeout(_deltaTimer); _deltaTimer = null; }
    const text = _pendingDelta;
    _pendingDelta = "";
    _enqueueSse("content_block_delta", text, null);
}
