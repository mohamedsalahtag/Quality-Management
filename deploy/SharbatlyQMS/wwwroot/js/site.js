// Global form-submit guard: after a form starts submitting, disable any
// submit button inside it so a double-click on a slow network cannot
// produce duplicate POSTs. Honors a `data-no-double-guard` opt-out for the
// rare form that intercepts submit and wants to keep the button
// interactive (e.g. inline AJAX forms that already manage their own state).
(function () {
    document.addEventListener('submit', function (ev) {
        var form = ev.target;
        if (!form || form.tagName !== 'FORM') return;
        if (form.hasAttribute('data-no-double-guard')) return;
        var btns = form.querySelectorAll('button[type="submit"], input[type="submit"]');
        // setTimeout 0 -- defer until after the browser collects the form
        // payload, otherwise the disabled button's name=value is dropped.
        setTimeout(function () {
            btns.forEach(function (b) {
                if (b.disabled) return;
                b.dataset._origHtml = b.innerHTML;
                b.disabled = true;
                if (b.tagName === 'BUTTON' && !b.querySelector('.spinner-border')) {
                    b.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>' + b.innerHTML;
                }
            });
        }, 0);
    }, true);
})();
