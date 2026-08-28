// Confirm forms/buttons that carry data-confirm before submit (correction execution gate).
document.addEventListener("submit", function (event) {
    var form = event.target;
    if (!(form instanceof HTMLFormElement)) {
        return;
    }

    var submitter = event.submitter;
    var message = null;
    if (submitter instanceof HTMLElement) {
        message = submitter.getAttribute("data-confirm");
    }
    if (!message) {
        message = form.getAttribute("data-confirm");
    }
    if (!message) {
        return;
    }

    if (!window.confirm(message)) {
        event.preventDefault();
        event.stopPropagation();
        var confirmField = form.querySelector('[name="ConfirmCorrectionExecution"]');
        if (confirmField instanceof HTMLInputElement) {
            confirmField.value = "false";
        }
    }
});
