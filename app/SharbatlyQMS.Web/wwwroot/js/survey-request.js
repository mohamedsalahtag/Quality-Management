// The request-a-survey dialog (Views/Shared/_SurveyRequestModal.cshtml).
//
// Two ways in, one request:
//   * arrival page -- answering Joint Survey = Yes opens the dialog. Cancelling
//     puts the answer back; confirming creates the survey and locks the answer
//     at Yes WITHOUT reloading, so anything else typed into the shipment form
//     is still there to be saved.
//   * quality order page -- the Potential Survey button opens it; on success
//     the page reloads to show the UNDER SURVEY banner.
//
// The server decides everything: who may ask, the one-survey-per-arrival rule,
// and the minimum reason. This script only collects the two text boxes.
(function () {
    "use strict";
    var modalEl = document.getElementById("surveyRequestModal");
    if (!modalEl || typeof bootstrap === "undefined") return;

    var modal     = new bootstrap.Modal(modalEl);
    var reasonEl  = document.getElementById("surveyRequestReason");
    var commentEl = document.getElementById("surveyRequestComments");
    var errorEl   = document.getElementById("surveyRequestError");
    var submitEl  = document.getElementById("surveyRequestSubmit");
    var spinner   = submitEl.querySelector(".spinner-border");
    var linesBox  = document.getElementById("surveyRequestLines");
    var token     = (document.querySelector("#surveyRequestToken input[name=__RequestVerificationToken]") || {}).value || "";
    var MIN_REASON = 5;
    var done = false;          // a survey now exists for this container
    var linesLoaded = false;

    // Joint Survey radios on the arrival form (absent on the quality order page).
    var yes = document.getElementById("JointSurvey-y");
    var no  = document.getElementById("JointSurvey-n");
    var before = null;         // which radio was ticked before Yes was picked

    function setError(msg) {
        errorEl.textContent = msg || "";
        errorEl.hidden = !msg;
    }
    function refreshSubmit() {
        submitEl.disabled = reasonEl.value.trim().length < MIN_REASON;
    }
    function pct(v) { return Number(v).toFixed(2) + "%"; }

    function loadLines() {
        var url = modalEl.getAttribute("data-url-lines");
        if (!url || linesLoaded) return;
        linesLoaded = true;
        fetch(url, { credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (d) {
                if (!d || !d.ok || !d.lines || !d.lines.length) return;
                var body = linesBox.querySelector("tbody");
                d.lines.forEach(function (l) {
                    var tr = document.createElement("tr");
                    var name = document.createElement("td");
                    name.textContent = l.item + (l.total ? " (total)" : "");
                    name.title = l.group;
                    tr.appendChild(name);
                    [l.result, l.tolerance, l.over].forEach(function (v, i) {
                        var td = document.createElement("td");
                        td.className = "text-end" + (i === 0 ? " text-danger fw-semibold" : "");
                        td.textContent = pct(v);
                        tr.appendChild(td);
                    });
                    body.appendChild(tr);
                });
                linesBox.hidden = false;
            })
            .catch(function () { /* the list is a convenience; the request works without it */ });
    }

    function open() {
        setError("");
        refreshSubmit();
        loadLines();
        modal.show();
    }

    reasonEl.addEventListener("input", refreshSubmit);
    modalEl.addEventListener("shown.bs.modal", function () { reasonEl.focus(); });

    // Closed without a survey: the Joint Survey answer goes back to what it was.
    modalEl.addEventListener("hidden.bs.modal", function () {
        if (done || !yes) return;
        yes.checked = false;
        if (no) no.checked = (before === "no");
    });

    if (yes) {
        if (no && no.checked) before = "no";
        if (no) no.addEventListener("change", function () { if (no.checked) before = "no"; });
        yes.addEventListener("change", function () {
            if (yes.checked && !done) open();
        });
    }
    // Explicit buttons: Potential Survey on the quality order, and "Request
    // survey" beside an old Joint Survey = Yes answer that has no survey.
    document.querySelectorAll("[data-survey-request]").forEach(function (b) {
        b.addEventListener("click", function () { open(); });
    });

    submitEl.addEventListener("click", function () {
        if (reasonEl.value.trim().length < MIN_REASON) return;
        setError("");
        submitEl.disabled = true;
        spinner.hidden = false;

        var fd = new FormData();
        var qoId = modalEl.getAttribute("data-qo-id");
        if (qoId) fd.append("qualityOrderId", qoId);
        else fd.append("arrivalId", modalEl.getAttribute("data-arrival-id"));
        fd.append("reason", reasonEl.value.trim());
        fd.append("comments", commentEl.value.trim());
        fd.append("__RequestVerificationToken", token);

        fetch(modalEl.getAttribute("data-url"), { method: "POST", body: fd, credentials: "same-origin" })
            .then(function (r) {
                if (r.status === 403) return { ok: false, error: "You are not allowed to request a survey for this container." };
                if (!r.ok) return { ok: false, error: "The survey could not be requested (error " + r.status + "). Please try again." };
                return r.json();
            })
            .then(function (d) {
                spinner.hidden = true;
                if (!d || !d.ok) {
                    setError((d && d.error) || "The survey could not be requested. Please try again.");
                    refreshSubmit();
                    return;
                }
                done = true;
                modal.hide();
                if (qoId) { window.location.reload(); return; }
                lockJointSurvey(d);
                if (window.appDialogs) window.appDialogs.alert(d.message, { kind: "success" });
            })
            .catch(function () {
                spinner.hidden = true;
                setError("The survey could not be requested. Check the connection and try again.");
                refreshSubmit();
            });
    });

    // The arrival form stays as the user left it; only the Joint Survey answer
    // changes, to a locked Yes with the survey's number beside it.
    function lockJointSurvey(d) {
        if (yes) { yes.checked = true; yes.disabled = true; }
        if (no)  { no.checked = false; no.disabled = true; }
        var group = yes ? yes.closest(".yn-toggle") : null;
        if (group) group.classList.remove("yn-unanswered");
        var note = document.getElementById("jointSurveyNote");
        if (!note) return;
        note.textContent = "";
        var badge = document.createElement("span");
        badge.className = "badge bg-warning text-dark";
        badge.textContent = d.surveyNo + " · " + d.statusLabel;
        note.appendChild(badge);
        if (d.url) {
            var a = document.createElement("a");
            a.href = d.url;
            a.className = "ms-2 small";
            a.textContent = "View survey";
            note.appendChild(a);
        }
        document.querySelectorAll("[data-survey-request]").forEach(function (b) { b.hidden = true; });
    }
})();
