window.quinceAuth = {
    logout: async function () {
        try {
            await fetch('/api/auth/logout', { method: 'POST' });
        } finally {
            window.location.href = '/login';
        }
    },
};
