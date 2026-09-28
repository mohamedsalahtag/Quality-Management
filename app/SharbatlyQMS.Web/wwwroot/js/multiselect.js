// multiselect: keep a filter dropdown's button text honest while it is open.
//
// The control itself is plain HTML — same-named checkboxes inside a Bootstrap
// dropdown — so it submits, binds and round-trips with no script at all. This
// file exists only so the button stops saying "All plants" the moment you tick
// something, instead of waiting for the page to reload.
//
// Opt in from the partial:
//   <div class="dropdown" data-multiselect>
//     <button data-ms-summary data-ms-all="All plants">All plants</button>
//     ... <input type="checkbox" name="plant" value="JD01"> ...
//     <button data-ms-clear>Clear</button>
//
// The server renders the same summary on load, so what you read after a reload
// and what you read mid-edit are produced by the same rule.
(function () {
    function summarise(root) {
        var button = root.querySelector('[data-ms-summary]');
        if (!button) return;

        var boxes   = root.querySelectorAll('input[type=checkbox]');
        var checked = [];
        boxes.forEach(function (b) { if (b.checked) checked.push(b); });

        if (checked.length === 0) {
            button.textContent = button.getAttribute('data-ms-all') || 'All';
        } else if (checked.length === 1) {
            // The label, not the value: a plant reads as its name, not its code.
            var label = root.querySelector('label[for="' + checked[0].id + '"]');
            button.textContent = (label ? label.textContent : checked[0].value).trim();
        } else {
            button.textContent = checked.length + ' selected';
        }
    }

    function enhance(root) {
        root.addEventListener('change', function (e) {
            if (e.target && e.target.type === 'checkbox') summarise(root);
        });

        var clear = root.querySelector('[data-ms-clear]');
        if (clear) {
            clear.addEventListener('click', function () {
                root.querySelectorAll('input[type=checkbox]').forEach(function (b) { b.checked = false; });
                summarise(root);
            });
        }

        // Optional search box (data-ms-search): narrows the options to those
        // whose label contains the typed text, ignoring case. Ticked options
        // always stay visible, so narrowing the list can never hide what is
        // already selected.
        var search = root.querySelector('[data-ms-search]');
        if (search) {
            var noMatch = root.querySelector('[data-ms-nomatch]');
            var apply = function () {
                var q = search.value.trim().toLowerCase();
                var shown = 0;
                root.querySelectorAll('.form-check').forEach(function (row) {
                    var box   = row.querySelector('input[type=checkbox]');
                    var label = row.querySelector('label');
                    var text  = (label ? label.textContent : '').toLowerCase();
                    var show  = q === '' || (box && box.checked) || text.indexOf(q) !== -1;
                    row.classList.toggle('d-none', !show);
                    if (show) shown++;
                });
                if (noMatch) noMatch.classList.toggle('d-none', shown > 0);
            };
            search.addEventListener('input', apply);
            // Enter would submit the whole filter form mid-search.
            search.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') e.preventDefault();
            });
            // Ready to type the moment the menu opens; cleared each time so the
            // next visit starts with the full list.
            root.addEventListener('shown.bs.dropdown', function () { search.focus(); });
            root.addEventListener('hidden.bs.dropdown', function () { search.value = ''; apply(); });
            if (clear) clear.addEventListener('click', function () { search.value = ''; apply(); });
        }
    }

    function init() {
        document.querySelectorAll('[data-multiselect]').forEach(enhance);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
