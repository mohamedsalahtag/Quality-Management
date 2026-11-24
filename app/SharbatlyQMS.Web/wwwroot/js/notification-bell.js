// The bell in the top bar (Views/Shared/_NotificationBell.cshtml).
// The unread count comes with the page; the newest few are fetched each time
// the dropdown opens, so the list is never stale and costs nothing until asked.
(function () {
    "use strict";
    var bell = document.getElementById("notifBell");
    if (!bell) return;
    var list  = document.getElementById("notifList");
    var count = document.getElementById("notifCount");
    var url   = bell.getAttribute("data-url-recent");

    function message(text) {
        list.textContent = "";
        var d = document.createElement("div");
        d.className = "px-3 py-3 text-muted small";
        d.textContent = text;
        list.appendChild(d);
    }

    function render(data) {
        if (count) {
            count.textContent = data.count > 99 ? "99+" : String(data.count);
            count.classList.toggle("d-none", !(data.count > 0));
        }
        if (!data.items || !data.items.length) { message("You have no notifications."); return; }
        list.textContent = "";
        data.items.forEach(function (n) {
            var a = document.createElement("a");
            a.href = n.url;
            a.className = "dropdown-item py-2 border-bottom" + (n.unread ? "" : " text-muted");
            a.style.whiteSpace = "normal";

            var title = document.createElement("div");
            title.className = n.unread ? "fw-semibold" : "";
            title.textContent = n.title;
            a.appendChild(title);

            if (n.body) {
                var body = document.createElement("div");
                body.className = "small";
                body.textContent = n.body;
                a.appendChild(body);
            }
            var at = document.createElement("div");
            at.className = "small text-muted";
            at.textContent = n.at;
            a.appendChild(at);
            list.appendChild(a);
        });
    }

    bell.addEventListener("show.bs.dropdown", function () {
        fetch(url, { credentials: "same-origin", headers: { "Accept": "application/json" } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (d) { if (d) render(d); else message("Notifications could not be loaded."); })
            .catch(function () { message("Notifications could not be loaded."); });
    });
})();
