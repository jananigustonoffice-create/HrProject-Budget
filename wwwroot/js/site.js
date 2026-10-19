// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.


// ---- count-up for headline numbers (elements with data-count)
(function () {
    if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    document.querySelectorAll('[data-count]').forEach(function (el, i) {
        var to = parseFloat(el.dataset.count) || 0, dec = +el.dataset.dec || 0, suf = el.dataset.suf || '', orig = el.textContent;
        var t0 = performance.now() + i * 70, d = 1000;
        function f(n) {
            var p = Math.min(Math.max((n - t0) / d, 0), 1), e = 1 - Math.pow(1 - p, 3);
            el.textContent = (to * e).toLocaleString(undefined, { minimumFractionDigits: dec, maximumFractionDigits: dec }) + suf;
            if (p < 1) requestAnimationFrame(f);
        }
        requestAnimationFrame(f);
        setTimeout(function () { el.textContent = orig; }, 1300 + i * 70);   // always end on the exact server-rendered value
    });
})();

// ---- quick search over a list table (input[data-filter] + table.js-filter)
(function () {
    var inp = document.querySelector('input[data-filter]'), tbl = document.querySelector('table.js-filter');
    if (!inp || !tbl || !tbl.tBodies.length) return;
    var rows = [].slice.call(tbl.tBodies[0].rows), out = document.getElementById('rowCount');
    function update() {
        var q = inp.value.trim().toLowerCase(), n = 0;
        rows.forEach(function (r) { var show = !q || r.textContent.toLowerCase().indexOf(q) > -1; r.style.display = show ? '' : 'none'; if (show) n++; });
        if (out) out.textContent = q ? n + ' of ' + rows.length + ' shown' : rows.length + ' records';
    }
    inp.addEventListener('input', update); update();
})();