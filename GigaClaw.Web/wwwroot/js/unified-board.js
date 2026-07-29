// Collapse-state persistence for the unified multi-project board (/board).
// One localStorage key per project slug so each browser remembers which lanes
// were collapsed independently of any other client.
const UNIFIED_BOARD_PREFIX = 'unified-board-collapsed-';

window.unifiedBoardStorage = {
    // Returns { [slug]: bool } for every slug passed in, reading localStorage once
    // per call instead of round-tripping per-lane from .NET.
    getCollapsed: function (slugs) {
        const result = {};
        (slugs || []).forEach(function (slug) {
            result[slug] = localStorage.getItem(UNIFIED_BOARD_PREFIX + slug) === '1';
        });
        return result;
    },

    setCollapsed: function (slug, collapsed) {
        try {
            localStorage.setItem(UNIFIED_BOARD_PREFIX + slug, collapsed ? '1' : '0');
        } catch {
            /* localStorage unavailable (private browsing, quota, ...) — collapse state
               simply won't persist across reloads; the in-memory state still works. */
        }
    }
};
