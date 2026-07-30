// security-matrix: interaction for Admin > Security.
//
// Three jobs, all of them about making a ~100-row permission list workable:
//   * search across every screen card at once,
//   * keep the "granted / total" counters honest as boxes are ticked,
//   * make the Read-only / Edit switch a GENERATOR -- picking read-only unticks
//     that screen's buttons, so the screen can never show a role as having a
//     button it could not press. The server enforces the same rule, so this is
//     an honesty aid rather than the security boundary.
//
// Vanilla, like every other script here. No build step.
(function () {
    var form = document.getElementById('grantsForm');

    // ---- Screen access level -> hidden "screenKey=level" values -------------
    // Radio groups can't post a composite value on their own, so the level is
    // serialised into hidden inputs on submit. Sparse: level 0 posts nothing,
    // and the server treats a missing value as "no access".
    function serialiseLevels() {
        if (!form) return;
        form.querySelectorAll('input[data-serialised-level]').forEach(function (n) { n.remove(); });

        form.querySelectorAll('[data-screen-level]').forEach(function (group) {
            var key = group.getAttribute('data-screen-level');
            var picked = group.querySelector('input[type=radio]:checked');
            if (!picked || picked.value === '0') return;
            addHidden('screenLevel', key + '=' + picked.value);
        });
        form.querySelectorAll('[data-screen-toggle]').forEach(function (cb) {
            if (!cb.checked) return;
            addHidden('screenLevel', cb.getAttribute('data-screen-toggle') + '=2');
        });
    }

    function addHidden(name, value) {
        var i = document.createElement('input');
        i.type = 'hidden';
        i.name = name;
        i.value = value;
        i.setAttribute('data-serialised-level', '');
        form.appendChild(i);
    }

    if (form) form.addEventListener('submit', serialiseLevels);

    // ---- Read-only generates: untick the screen's buttons ------------------
    function applyLevelToCard(card) {
        var group = card.querySelector('[data-screen-level]');
        if (!group) return;
        var picked = group.querySelector('input[type=radio]:checked');
        var level  = picked ? picked.value : '0';
        var boxes  = card.querySelectorAll('.perm-row input[type=checkbox]:not([disabled])');

        if (level !== '2') {
            // No access or read-only: a button here could never be pressed, so
            // showing it ticked would be a lie.
            boxes.forEach(function (b) { b.checked = false; });
        }
        boxes.forEach(function (b) { b.disabled = (level !== '2'); });
        card.querySelectorAll('[data-select-all],[data-select-none]').forEach(function (b) {
            b.disabled = (level !== '2');
        });
    }

    document.querySelectorAll('[data-card]').forEach(function (card) {
        var group = card.querySelector('[data-screen-level]');
        if (group) {
            group.addEventListener('change', function () { applyLevelToCard(card); updateCount(card); updateTotal(); });
            applyLevelToCard(card);
        }
        card.querySelectorAll('input[type=checkbox]').forEach(function (cb) {
            cb.addEventListener('change', function () { updateCount(card); updateTotal(); });
        });
        var all = card.querySelector('[data-select-all]');
        var none = card.querySelector('[data-select-none]');
        if (all)  all.addEventListener('click',  function () { setAll(card, true);  });
        if (none) none.addEventListener('click', function () { setAll(card, false); });
        updateCount(card);
    });

    function setAll(card, on) {
        card.querySelectorAll('.perm-row input[type=checkbox]:not([disabled])')
            .forEach(function (b) { b.checked = on; });
        updateCount(card);
        updateTotal();
    }

    function cardCounts(card) {
        var granted = 0, total = 0;
        var group  = card.querySelector('[data-screen-level]');
        var toggle = card.querySelector('[data-screen-toggle]');
        if (group) {
            total++;
            var picked = group.querySelector('input[type=radio]:checked');
            if (picked && picked.value !== '0') granted++;
        } else if (toggle) {
            total++;
            if (toggle.checked) granted++;
        }
        card.querySelectorAll('.perm-row input[type=checkbox]').forEach(function (b) {
            total++;
            if (b.checked) granted++;
        });
        return { granted: granted, total: total };
    }

    function updateCount(card) {
        var badge = card.querySelector('[data-count]');
        if (!badge) return;
        var c = cardCounts(card);
        badge.textContent = c.granted + ' / ' + c.total;
        badge.classList.toggle('bg-success-subtle', c.granted > 0);
        badge.classList.toggle('text-success-emphasis', c.granted > 0);
        badge.classList.toggle('bg-secondary-subtle', c.granted === 0);
        badge.classList.toggle('text-secondary-emphasis', c.granted === 0);
    }

    function updateTotal() {
        var out = document.getElementById('grantTotal');
        if (!out) return;
        var g = 0, t = 0;
        document.querySelectorAll('[data-card]').forEach(function (card) {
            var c = cardCounts(card);
            g += c.granted; t += c.total;
        });
        out.textContent = g + ' of ' + t + ' permissions granted';
    }
    updateTotal();

    // ---- Search across every card ------------------------------------------
    var search = document.getElementById('permSearch');
    if (search) {
        search.addEventListener('input', function () {
            var q = search.value.trim().toLowerCase();
            document.querySelectorAll('[data-card]').forEach(function (card) {
                var title = (card.querySelector('.card-header strong') || {}).textContent || '';
                var cardHit = !q || title.toLowerCase().indexOf(q) !== -1;
                var anyRowHit = false;

                card.querySelectorAll('.perm-row').forEach(function (row) {
                    var hit = !q || (row.getAttribute('data-label') || '').toLowerCase().indexOf(q) !== -1;
                    row.style.display = (hit || cardHit) ? '' : 'none';
                    if (hit) anyRowHit = true;
                });

                card.style.display = (cardHit || anyRowHit) ? '' : 'none';
                // Open matching cards so the hit is visible without a second click.
                var body = card.querySelector('.collapse');
                if (body && q && (cardHit || anyRowHit)) body.classList.add('show');
            });
        });
    }

    document.querySelectorAll('[data-expand-all]').forEach(function (b) {
        b.addEventListener('click', function () {
            document.querySelectorAll('[data-card] .collapse').forEach(function (c) { c.classList.add('show'); });
        });
    });
    document.querySelectorAll('[data-collapse-all]').forEach(function (b) {
        b.addEventListener('click', function () {
            document.querySelectorAll('[data-card] .collapse').forEach(function (c) { c.classList.remove('show'); });
        });
    });

    // ---- Comparison grid filter --------------------------------------------
    var mSearch = document.getElementById('matrixSearch');
    if (mSearch) {
        mSearch.addEventListener('input', function () {
            var q = mSearch.value.trim().toLowerCase();
            document.querySelectorAll('#matrixTable tbody tr').forEach(function (tr) {
                var hit = !q || (tr.getAttribute('data-label') || '').toLowerCase().indexOf(q) !== -1;
                tr.style.display = hit ? '' : 'none';
            });
        });
    }
})();
