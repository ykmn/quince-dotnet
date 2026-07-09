// Drives the hidden <audio> element used for the browser-side "listen to channel" feature.
// Playback always goes to the browser's/OS's current default output device — picking a specific
// device would need HTMLMediaElement.setSinkId(), which browsers restrict to secure contexts
// (HTTPS, or localhost) — deferred until the app is served over HTTPS.
window.quincePlayback = (function () {
    "use strict";

    let dotNetRef = null;
    let listenersAttached = false;

    function element() {
        return document.getElementById("quince-playback-audio");
    }

    // Reports back to MainLayout.OnPlaybackStarted/OnPlaybackError so it can stop the "Буферизация"
    // status-bar spinner (MainLayout.razor) — attached once, the audio element is reused for the
    // whole app lifetime so a fresh listener per play() call would just keep piling up.
    function ensureListeners(audio) {
        if (listenersAttached) return;
        audio.addEventListener("playing", function () {
            if (dotNetRef) dotNetRef.invokeMethodAsync("OnPlaybackStarted");
        });
        audio.addEventListener("error", function () {
            if (dotNetRef) dotNetRef.invokeMethodAsync("OnPlaybackError");
        });
        listenersAttached = true;
    }

    function play(url, dotNetHelper) {
        const audio = element();
        if (!audio) return;
        dotNetRef = dotNetHelper;
        ensureListeners(audio);
        audio.src = url;
        audio.play().catch(function (err) {
            console.error("Quince: playback failed to start", err);
            if (dotNetRef) dotNetRef.invokeMethodAsync("OnPlaybackError");
        });
    }

    function stop() {
        const audio = element();
        if (!audio) return;
        audio.pause();
        audio.removeAttribute("src");
        audio.load();
        dotNetRef = null;
    }

    return { play: play, stop: stop };
})();
