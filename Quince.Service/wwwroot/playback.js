// Drives the hidden <audio> element used for the browser-side "listen to channel" feature.
// Playback always goes to the browser's/OS's current default output device — picking a specific
// device would need HTMLMediaElement.setSinkId(), which browsers restrict to secure contexts
// (HTTPS, or localhost) — deferred until the app is served over HTTPS.
window.quincePlayback = (function () {
    "use strict";

    function element() {
        return document.getElementById("quince-playback-audio");
    }

    function play(url) {
        const audio = element();
        if (!audio) return;
        audio.src = url;
        audio.play().catch(function (err) {
            console.error("Quince: playback failed to start", err);
        });
    }

    function stop() {
        const audio = element();
        if (!audio) return;
        audio.pause();
        audio.removeAttribute("src");
        audio.load();
    }

    return { play: play, stop: stop };
})();
