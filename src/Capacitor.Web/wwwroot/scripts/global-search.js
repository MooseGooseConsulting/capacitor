let listener;

export function register(component) {
    listener = event => {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
            event.preventDefault();
            component.invokeMethodAsync("OpenGlobalSearchAsync");
        }
    };
    document.addEventListener("keydown", listener);
}

export function unregister() {
    if (listener) document.removeEventListener("keydown", listener);
    listener = undefined;
}

export function focus(id) {
    document.getElementById(id)?.focus();
}
