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

    // ---- The screen level drives which buttons can be granted --------------
    // Edit (2):        every button is grantable.
    // Read only (1):   only READ-only buttons (download the PDF, export) can be
    //                  granted -- an edit button could never be pressed, so it
    //                  is unticked and locked out. This is what lets an admin
    //                  build a "can look, can print, cannot change" role.
    // No access (0):   the screen is off, so nothing on it is reachable.
    //
    // Boxes are selected WITHOUT :not([disabled]) on purpose: a box a previous
    // Read-only / No-access choice disabled must be re-enabled when the screen
    // goes back to Edit. [data-locked] boxes (the administrator floor) are the
    // only ones left untouched -- they are permanently ticked by the server.
    function applyLevelToCard(card) {
        var group = card.querySelector('[data-screen-level]');
        if (!group) return;
        var picked = group.querySelector('input[type=radio]:checked');
        var level  = picked ? picked.value : '0';
        var anyGrantable = false;

        card.querySelectorAll('.perm-row input[type=checkbox]:not([data-locked])').forEach(function (b) {
            var readOnlyAction = b.hasAttribute('data-readonly');
            var grantable = level === '2' || (level === '1' && readOnlyAction);
            if (!grantable) b.checked = false;   // can't grant what can't be reached
            b.disabled = !grantable;
            if (grantable) anyGrantable = true;
        });

        card.querySelectorAll('[data-select-all],[data-select-none]').forEach(function (b) {
            b.disabled = !anyGrantable;
        });

        // Restate what the current choice means, right under the buttons, so
        // "No access" and "Read only" are never a mystery.
        var hint = card.querySelector('[data-level-hint]');
        if (hint) {
            hint.textContent =
                level === '2' ? 'Edit: this role can open the screen and use every button you tick below.'
              : level === '1' ? (anyGrantable
                    ? 'Read only: this role can view the screen. Tick the view/export buttons it may use; buttons that change data are switched off.'
                    : 'Read only: this role can view the screen. It has no view/export buttons, so nothing else can be granted here.')
              : 'No access: this role cannot open or even see this screen, so none of its buttons apply.';
        }
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

    // ---- Editable comparison grid: the two-switch generator, per column -----
    // Each role column is an independent per-role editor. A screen <select>
    // (No access / Read only / Edit) drives which of THAT role's action cells in
    // THAT screen can be granted, mirroring applyLevelToCard() above:
    //   Edit (2)      -> every action grantable
    //   Read only (1) -> only view/export actions; edit actions unticked + locked
    //   No access (0) -> nothing on the screen is reachable, all unticked + locked
    function levelOf(sel) {
        var v = sel.value || '';
        return v.substring(v.lastIndexOf('=') + 1);   // "screenKey=2" -> "2"
    }
    function applyMatrixScreen(sel) {
        var role   = sel.getAttribute('data-role');
        var screen = sel.getAttribute('data-screen');
        var level  = levelOf(sel);
        document
          .querySelectorAll('.matrix-action[data-role="' + role + '"][data-screen="' + screen + '"]')
          .forEach(function (cb) {
              var readOnly  = cb.getAttribute('data-readonly') === 'true';
              var grantable = level === '2' || (level === '1' && readOnly);
              if (!grantable) cb.checked = false;   // can't grant what can't be reached
              cb.disabled = !grantable;
          });
    }
    var screenSelects = document.querySelectorAll('.matrix-screen');
    screenSelects.forEach(function (sel) {
        sel.addEventListener('change', function () { applyMatrixScreen(sel); });
        applyMatrixScreen(sel);   // set the initial enabled/disabled state
    });
})();
