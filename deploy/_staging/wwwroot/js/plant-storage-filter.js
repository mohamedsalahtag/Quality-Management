// plant-storage-filter: narrow a storage-location <select> to the plant chosen
// in a partner <select>.
//
// SAP storage-location codes repeat across plants ("0001" exists under several),
// so showing every code un-scoped is ambiguous. Each storage <option> carries
// its owning plant in data-plant; picking a plant hides the rest.
//
// Opt in by pairing the two controls with a shared group name:
//   <select id="..." data-plant-filter="pending">        <!-- the plant control -->
//   <select id="..." data-storage-filter="pending">      <!-- the storage control -->
// The plant control may also be a hidden input (plant-scoped operators see a
// locked badge instead of a dropdown) — it still narrows correctly on load.
//
// Used by Pending Containers and the Quality Orders filter panel.
(function () {
    function wire(plantEl, storageEl) {
        function apply() {
            var plant = plantEl.value;
            var anyVisible = false;
            Array.prototype.forEach.call(storageEl.options, function (opt) {
                if (!opt.value) { opt.hidden = false; anyVisible = true; return; }   // the "All" placeholder
                var optPlant = opt.getAttribute('data-plant') || '';
                var match    = !plant || optPlant === plant;
                opt.hidden = !match;
                if (match) anyVisible = true;
            });
            // If the current selection just became hidden, snap back to "All"
            // so what submits matches what the user can see.
            var sel = storageEl.options[storageEl.selectedIndex];
            if (sel && sel.hidden) storageEl.value = '';
            // Nothing under this plant -> disable rather than offer an empty list.
            storageEl.disabled = !anyVisible;
        }

        plantEl.addEventListener('change', apply);
        // Run once on load so a bookmarked ?plant=X URL starts already narrowed.
        apply();
    }

    function init() {
        document.querySelectorAll('[data-plant-filter]').forEach(function (plantEl) {
            var group = plantEl.getAttribute('data-plant-filter');
            var storageEl = document.querySelector('[data-storage-filter="' + group + '"]');
            if (storageEl) wire(plantEl, storageEl);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
