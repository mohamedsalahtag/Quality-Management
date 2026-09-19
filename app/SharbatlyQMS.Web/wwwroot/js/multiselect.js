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
