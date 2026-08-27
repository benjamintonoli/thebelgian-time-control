// Confirm forms that carry data-confirm before submit (correction execution gate).
document.addEventListener("submit", function (event) {
    var form = event.target;
    if (!(form instanceof HTMLFormElement)) {
        return;
    }

    var message = form.getAttribute("data-confirm");
    if (!message) {
        return;
    }

    if (!window.confirm(message)) {
        event.preventDefault();
        event.stopPropagation();
    }
});
