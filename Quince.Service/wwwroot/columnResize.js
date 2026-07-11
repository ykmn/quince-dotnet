window.quinceColumnResize = {
    // Attaches drag-to-resize behaviour to every <th class="col-resizer-host"> in the given table and
    // restores widths saved from a previous session. Idempotent (guarded by dataset.resizeInit) so it's
    // safe to call from OnAfterRenderAsync on every render, not just the first — Blazor Server re-renders
    // the table's <thead> far less often than its <tbody>, but calling this again should never double up
    // the drag listeners or lose whatever width the user already picked.
    init: function (tableId) {
        var table = document.getElementById(tableId);
        if (!table || table.dataset.resizeInit) return;
        table.dataset.resizeInit = '1';

        var storageKey = 'quince-col-widths-' + tableId;
        var saved = {};
        try { saved = JSON.parse(localStorage.getItem(storageKey) || '{}'); } catch (e) { /* private mode, etc. */ }

        var headers = table.querySelectorAll('thead th');
        headers.forEach(function (th, index) {
            if (saved[index]) th.style.width = saved[index] + 'px';

            var resizer = th.querySelector('.col-resizer');
            if (!resizer) return;

            var startX = 0;
            var startWidth = 0;

            function onMouseMove(e) {
                var width = Math.max(30, startWidth + (e.pageX - startX));
                th.style.width = width + 'px';
            }

            function onMouseUp() {
                document.removeEventListener('mousemove', onMouseMove);
                document.removeEventListener('mouseup', onMouseUp);
                document.body.style.removeProperty('cursor');
                saved[index] = th.offsetWidth;
                try { localStorage.setItem(storageKey, JSON.stringify(saved)); } catch (e) { }
            }

            resizer.addEventListener('mousedown', function (e) {
                startX = e.pageX;
                startWidth = th.offsetWidth;
                document.body.style.cursor = 'col-resize';
                document.addEventListener('mousemove', onMouseMove);
                document.addEventListener('mouseup', onMouseUp);
                e.preventDefault();
                e.stopPropagation(); // don't trigger row selection under the header
            });
        });
    },
};
