// Goniometer (stereo phase/vectorscope) canvas renderer. No build step; loaded via <script>.
window.quinceGoniometer = (function () {
    "use strict";

    const BG = "#12161c";
    const GRID = "rgba(255,255,255,0.18)";
    const DOT = "#4fd1c5";

    function drawGrid(ctx, w, h) {
        const cx = w / 2, cy = h / 2;
        const r = Math.min(w, h) / 2 * 0.75;

        ctx.save();
        ctx.strokeStyle = GRID;
        ctx.lineWidth = 1;

        // Crosshair.
        ctx.beginPath();
        ctx.moveTo(cx - r, cy);
        ctx.lineTo(cx + r, cy);
        ctx.moveTo(cx, cy - r);
        ctx.lineTo(cx, cy + r);
        ctx.stroke();

        // Reference circle.
        ctx.beginPath();
        ctx.arc(cx, cy, r, 0, Math.PI * 2);
        ctx.stroke();

        ctx.restore();
    }

    function paintBackground(ctx, w, h) {
        ctx.fillStyle = BG;
        ctx.fillRect(0, 0, w, h);
    }

    function init(canvas) {
        if (!canvas) return;
        const ctx = canvas.getContext("2d");
        if (!ctx) return;
        paintBackground(ctx, canvas.width, canvas.height);
        drawGrid(ctx, canvas.width, canvas.height);
    }

    function draw(canvas, left, right) {
        if (!canvas) return;
        const ctx = canvas.getContext("2d");
        if (!ctx) return;

        const w = canvas.width, h = canvas.height;
        const cx = w / 2, cy = h / 2;
        const scale = Math.min(w, h) / 2 * 0.7;

        paintBackground(ctx, w, h);
        drawGrid(ctx, w, h);

        if (!left || !right) return;
        const n = Math.min(left.length, right.length);
        if (n === 0) return;

        ctx.fillStyle = DOT;
        for (let i = 0; i < n; i++) {
            const l = left[i];
            const r = right[i];

            // Standard goniometer (Lissajous) transform: 45-degree rotation so fully
            // correlated mono content draws as a vertical line. Raw samples are full-scale
            // float PCM (roughly [-1, 1]), and x/y can reach +-2 for fully anti/correlated
            // extremes, so clamp the final pixel position rather than the raw x/y — that
            // keeps normal stereo-width content undistorted while still guaranteeing a
            // clipping signal never draws outside the canvas.
            const x = (r - l);
            const y = -(l + r);

            const px = Math.max(0, Math.min(w, cx + x * scale));
            const py = Math.max(0, Math.min(h, cy + y * scale));

            ctx.fillRect(px - 1, py - 1, 2, 2);
        }
    }

    return { init, draw };
})();
