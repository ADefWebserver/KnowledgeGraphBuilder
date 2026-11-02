using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace KnowledgeGraphBuilder.Shared.Services;

/// <summary>Wraps <c>wwwroot/kgb.js</c>.</summary>
public sealed class BrowserInterop(IJSRuntime js)
{
    public ValueTask<bool> WatchWidthAsync<T>(DotNetObjectReference<T> target, int breakpoint) where T : class =>
        js.InvokeAsync<bool>("kgb.watchWidth", target, breakpoint);

    public ValueTask RegisterShortcutsAsync<T>(DotNetObjectReference<T> target) where T : class =>
        js.InvokeVoidAsync("kgb.registerShortcuts", target);

    public ValueTask SetUnsavedAsync(bool unsaved) => js.InvokeVoidAsync("kgb.setUnsaved", unsaved);

    public ValueTask FocusAsync(string selector) => js.InvokeVoidAsync("kgb.focus", selector);

    public ValueTask ScrollToBottomAsync(string selector) => js.InvokeVoidAsync("kgb.scrollToBottom", selector);

    public ValueTask BindComposerAsync<T>(string id, DotNetObjectReference<T> target) where T : class =>
        js.InvokeVoidAsync("kgb.bindComposer", id, target);

    public ValueTask ClearValueAsync(string id) => js.InvokeVoidAsync("kgb.clearValue", id);

    public ValueTask BindGraphAsync(ElementReference svg) => js.InvokeVoidAsync("kgb.bindGraph", svg);

    public ValueTask FitGraphAsync(ElementReference svg) => js.InvokeVoidAsync("kgb.fitGraph", svg);

    public ValueTask BindTreeDragAsync<T>(ElementReference root, DotNetObjectReference<T> target) where T : class =>
        js.InvokeVoidAsync("kgb.bindTreeDrag", root, target);
}
