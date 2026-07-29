window.chatDrawerScrollToBottom = function (el) {
    if (el) el.scrollTop = el.scrollHeight;
};

// Keep ordinary typing entirely in the browser. Blazor Server only receives the
// completed value on Send / Inject / Enter, avoiding a SignalR event and rerender
// for every character.
window.chatDrawerComposer = {
    install: function (el, dotnetRef) {
        if (!el) return;
        el.__chatComposerDotnetRef = dotnetRef;
        if (!el.__chatComposerInstalled) {
            el.__chatComposerInstalled = true;
            el.addEventListener('input', function () {
                window.chatDrawerComposer.sync(el);
            });
            el.addEventListener('keydown', function (e) {
                if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
                e.preventDefault();

                const hasImages = el.dataset.hasImages === 'true';
                const value = el.value || '';
                if (!value.trim() && !hasImages) return;

                el.value = '';
                window.chatDrawerComposer.sync(el);
                const target = el.__chatComposerDotnetRef;
                if (target) target.invokeMethodAsync('SubmitComposerFromJs', value);
            });
        }
        window.chatDrawerComposer.sync(el);
    },

    sync: function (el) {
        if (!el) return;
        const area = el.closest('.chat-input-area');
        const button = area ? area.querySelector('[data-chat-submit="true"]') : null;
        if (!button) return;

        const hasImages = el.dataset.hasImages === 'true';
        button.disabled = el.disabled || (!el.value.trim() && !hasImages);
    },

    takeValue: function (el) {
        if (!el) return '';
        const value = el.value || '';
        el.value = '';
        window.chatDrawerComposer.sync(el);
        return value;
    }
};

// Image paste support (#115). Watches the chat textarea for `paste` events carrying
// image clipboard items, validates them client-side, and bridges accepted images back
// to the Blazor component via JSInvokable callbacks. Plain-text pastes pass through
// unchanged because preventDefault() only fires when at least one image item is found.
window.chatDrawerInstallPasteHandler = function (el, dotnetRef) {
    if (!el || el.__pasteHandlerInstalled) return;
    el.__pasteHandlerInstalled = true;

    var ALLOWED = { 'image/jpeg': 1, 'image/png': 1, 'image/gif': 1, 'image/webp': 1 };
    var MAX_BYTES = 5 * 1024 * 1024; // 5 MB per image
    var MAX_IMAGES = 5;

    el.addEventListener('paste', function (e) {
        var cd = e.clipboardData;
        if (!cd || !cd.items) return;
        var imageItems = [];
        for (var i = 0; i < cd.items.length; i++) {
            var it = cd.items[i];
            if (it.kind === 'file' && it.type && it.type.indexOf('image/') === 0) imageItems.push(it);
        }
        if (imageItems.length === 0) return; // let plain-text paste work normally

        e.preventDefault();

        if (imageItems.length > MAX_IMAGES) {
            dotnetRef.invokeMethodAsync('OnImagePasteError', 'too many images pasted at once (max ' + MAX_IMAGES + ')');
            return;
        }

        imageItems.forEach(function (item) {
            var file = item.getAsFile();
            if (!file) return;
            if (!ALLOWED[file.type]) {
                dotnetRef.invokeMethodAsync('OnImagePasteError', 'unsupported image type: ' + file.type);
                return;
            }
            if (file.size > MAX_BYTES) {
                dotnetRef.invokeMethodAsync('OnImagePasteError', 'image too large (max 5 MB)');
                return;
            }
            var reader = new FileReader();
            reader.onload = function () {
                dotnetRef.invokeMethodAsync('OnImagePasted', {
                    dataUrl: reader.result,
                    mime: file.type,
                    name: file.name || 'pasted-image',
                    sizeBytes: file.size
                });
            };
            reader.onerror = function () {
                dotnetRef.invokeMethodAsync('OnImagePasteError', 'failed to read image');
            };
            reader.readAsDataURL(file);
        });
    });
};
