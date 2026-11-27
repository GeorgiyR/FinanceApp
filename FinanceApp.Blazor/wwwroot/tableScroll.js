(() => {
    const selectors = [
        '.positions-table-wrap',
        '.tracked-stocks-table-wrap',
        '.orders-table-wrap',
        '.transactions-table-wrap',
        '.index-constituents-table-wrap',
        '.indices-table-wrap',
        '.financial-metrics-table-wrap',
        '.system-runs-table-wrap'
    ];
    const mirrors = new Map();
    let timer = null;

    function syncMirrors() {
        timer = null;
        for (const wrap of document.querySelectorAll(selectors.join(','))) {
            let mirror = mirrors.get(wrap);
            if (!mirror || !mirror.isConnected) {
                mirror = document.createElement('div');
                mirror.className = 'table-hscroll';
                mirror.innerHTML = '<div class="table-hscroll-inner"></div>';
                wrap.parentElement.insertBefore(mirror, wrap);
                mirrors.set(wrap, mirror);

                mirror.addEventListener('scroll', () => {
                    if (wrap.scrollLeft !== mirror.scrollLeft) wrap.scrollLeft = mirror.scrollLeft;
                });
                wrap.addEventListener('scroll', () => {
                    if (mirror.scrollLeft !== wrap.scrollLeft) mirror.scrollLeft = wrap.scrollLeft;
                });
            }

            const inner = mirror.firstElementChild;
            const overflowing = wrap.scrollWidth > wrap.clientWidth + 1;
            mirror.hidden = !overflowing;
            if (overflowing) {
                const scrollbarWidth = mirror.offsetWidth - mirror.clientWidth;
                const width = `${wrap.scrollWidth - scrollbarWidth}px`;
                if (inner.style.width !== width) inner.style.width = width;
            }
        }
    }

    function scheduleUpdate() {
        if (timer !== null) return;
        timer = window.setTimeout(syncMirrors, 80);
    }

    window.addEventListener('resize', scheduleUpdate);
    window.visualViewport?.addEventListener('resize', scheduleUpdate);
    document.addEventListener('DOMContentLoaded', scheduleUpdate);
    new MutationObserver(scheduleUpdate).observe(document.documentElement, { childList: true, subtree: true });
    scheduleUpdate();
})();
