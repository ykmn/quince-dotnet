// blazor.server.js loads with autostart="false" (see _Host.cshtml) purely so this custom
// reconnectionOptions can be passed — the default retry schedule (a handful of attempts on a short
// timer) gives up well before a laptop-sleep or Wi-Fi-switch gap has a chance to resolve itself.
Blazor.start({
    circuit: {
        reconnectionOptions: {
            maxRetries: 30,
            retryIntervalMilliseconds: 3000, // ~90s of active retrying before giving up
        },
    },
});

// Once the retry sequence is fully exhausted, or the server explicitly rejects the old circuit
// (e.g. DisconnectedCircuitRetentionPeriod elapsed while the tab was backgrounded/offline), that
// circuit is gone for good no matter how much longer the client keeps retrying — only a fresh page
// load gets a new one. Reload automatically instead of leaving the user staring at a dead
// "Переподключение..." banner they have to notice and manually refresh past.
(function () {
    var modal = document.getElementById('components-reconnect-modal');
    if (!modal) return;
    var observer = new MutationObserver(function () {
        if (modal.classList.contains('components-reconnect-failed') ||
            modal.classList.contains('components-reconnect-rejected')) {
            location.reload();
        }
    });
    observer.observe(modal, { attributes: true, attributeFilter: ['class'] });
})();
