window.quinceTheme = {
    storageKey: 'quince-theme',

    get: function () {
        try {
            return localStorage.getItem(this.storageKey) || 'light';
        } catch (e) {
            return 'light';
        }
    },

    set: function (theme) {
        document.documentElement.setAttribute('data-theme', theme);
        try {
            localStorage.setItem(this.storageKey, theme);
        } catch (e) {
            // localStorage unavailable (e.g. private mode) — theme still applies for this load,
            // just won't persist across reloads.
        }
    },
};
