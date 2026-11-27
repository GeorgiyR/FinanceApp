(() => {
    let timer = null;

    function updateTableHeights() {
        timer = null;
        const viewportHeight = window.visualViewport?.height || window.innerHeight;
        document.querySelectorAll('.positions-table-wrap.table-scroll-y').forEach((element) => {
            const rect = element.getBoundingClientRect();
            if (rect.width === 0) return;
            const available = viewportHeight - rect.top - 24;
            const maxHeight = Math.max(180, Math.min(available, viewportHeight * 0.8));
            element.style.maxHeight = `${Math.round(maxHeight)}px`;
        });
    }

    function scheduleUpdate() {
        if (timer !== null) return;
        timer = window.setTimeout(updateTableHeights, 80);
    }

    window.addEventListener('resize', scheduleUpdate);
    window.visualViewport?.addEventListener('resize', scheduleUpdate);
    document.addEventListener('DOMContentLoaded', scheduleUpdate);
    new MutationObserver(scheduleUpdate).observe(document.documentElement, { childList: true, subtree: true });
    scheduleUpdate();
})();
