// Every dialog/menu backdrop (.modal-overlay, .menu-overlay) already closes itself on click — Escape
// just simulates a click on the topmost one (last in DOM order — a nested dialog like
// FolderBrowserDialog renders after its parent, so it's naturally the last / topmost), reusing each
// component's existing close handler instead of wiring a new one per dialog. ChannelEditDialog's own
// overlay deliberately has no click-to-close handler (protects unsaved edits from an accidental
// backdrop click) — Escape correctly does nothing there either, for the same reason.
(function () {
    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;
        var overlays = document.querySelectorAll('.modal-overlay, .menu-overlay');
        if (overlays.length === 0) return;
        overlays[overlays.length - 1].click();
    });
})();
