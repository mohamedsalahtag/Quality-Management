// Image lightbox: click a gallery thumbnail to enlarge it, then navigate
// Prev / Next through the same gallery's images (arrow keys / on-screen
// buttons) and Close (× / Esc / click backdrop). Saves opening each image in
// its own tab. Works for AJAX-injected galleries too because it uses a single
// delegated click listener on document.
//
// Opt-in markup (see _ImageGalleryGrid.cshtml): anchors carry data-lightbox
// (+ optional data-caption) and href = full-image URL; a wrapper carries
// data-lightbox-group to scope prev/next to that gallery. The href is kept as
// a no-JS / middle-click fallback.
(function () {
    var overlay, imgEl, capEl, prevBtn, nextBtn, group = [], idx = 0;

    function injectCss() {
        if (document.getElementById('ig-lightbox-css')) return;
        var css = document.createElement('style');
        css.id = 'ig-lightbox-css';
        css.textContent =
            '.ig-lightbox{position:fixed;inset:0;z-index:2000;display:none;align-items:center;justify-content:center;' +
            'background:rgba(0,0,0,.85);padding:2rem;}' +
            '.ig-lightbox.open{display:flex;}' +
            'body.ig-lb-lock{overflow:hidden;}' +
            '.ig-lb-fig{margin:0;max-width:92vw;max-height:92vh;display:flex;flex-direction:column;align-items:center;gap:.5rem;}' +
            '.ig-lb-img{max-width:92vw;max-height:82vh;object-fit:contain;box-shadow:0 4px 30px rgba(0,0,0,.5);background:#fff;}' +
            '.ig-lb-cap{color:#eee;font-size:.9rem;text-align:center;max-width:80vw;}' +
            '.ig-lightbox button{position:absolute;background:rgba(0,0,0,.4);color:#fff;border:0;cursor:pointer;' +
            'border-radius:50%;width:3rem;height:3rem;font-size:1.6rem;line-height:1;display:flex;align-items:center;justify-content:center;}' +
            '.ig-lightbox button:hover{background:rgba(255,255,255,.25);}' +
            '.ig-lb-close{top:1rem;right:1rem;}' +
            '.ig-lb-prev{left:1rem;top:50%;transform:translateY(-50%);}' +
            '.ig-lb-next{right:1rem;top:50%;transform:translateY(-50%);}';
        document.head.appendChild(css);
    }

    function build() {
        injectCss();
        overlay = document.createElement('div');
        overlay.className = 'ig-lightbox';
        overlay.innerHTML =
            '<button class="ig-lb-close" aria-label="Close" title="Close (Esc)">×</button>' +
            '<button class="ig-lb-prev" aria-label="Previous" title="Previous (←)">‹</button>' +
            '<figure class="ig-lb-fig"><img class="ig-lb-img" alt=""><figcaption class="ig-lb-cap"></figcaption></figure>' +
            '<button class="ig-lb-next" aria-label="Next" title="Next (→)">›</button>';
        document.body.appendChild(overlay);
        imgEl = overlay.querySelector('.ig-lb-img');
        capEl = overlay.querySelector('.ig-lb-cap');
        prevBtn = overlay.querySelector('.ig-lb-prev');
        nextBtn = overlay.querySelector('.ig-lb-next');
        overlay.querySelector('.ig-lb-close').addEventListener('click', close);
        prevBtn.addEventListener('click', function (e) { e.stopPropagation(); step(-1); });
        nextBtn.addEventListener('click', function (e) { e.stopPropagation(); step(1); });
        // Click on the backdrop (not the image/buttons) closes.
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay || e.target.classList.contains('ig-lb-fig')) close();
        });
    }

    function show() {
        if (!overlay) build();
        var a = group[idx];
        imgEl.src = a.getAttribute('href');
        capEl.textContent = a.getAttribute('data-caption') || '';
        var multi = group.length > 1;
        prevBtn.style.display = multi ? '' : 'none';
        nextBtn.style.display = multi ? '' : 'none';
        overlay.classList.add('open');
        document.body.classList.add('ig-lb-lock');
    }

    function close() {
        if (!overlay) return;
        overlay.classList.remove('open');
        document.body.classList.remove('ig-lb-lock');
        imgEl.src = '';
    }

    function step(d) {
        if (group.length === 0) return;
        idx = (idx + d + group.length) % group.length;
        show();
    }

    document.addEventListener('click', function (e) {
        var a = e.target.closest ? e.target.closest('a[data-lightbox]') : null;
        if (!a) return;
        // Let modified clicks (new tab, etc.) behave normally.
        if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
        e.preventDefault();
        var wrap = a.closest('[data-lightbox-group]') || document;
        group = Array.prototype.slice.call(wrap.querySelectorAll('a[data-lightbox]'));
        idx = group.indexOf(a);
        if (idx < 0) { group = [a]; idx = 0; }
        show();
    });

    document.addEventListener('keydown', function (e) {
        if (!overlay || !overlay.classList.contains('open')) return;
        if (e.key === 'Escape') close();
        else if (e.key === 'ArrowLeft') step(-1);
        else if (e.key === 'ArrowRight') step(1);
    });
})();
