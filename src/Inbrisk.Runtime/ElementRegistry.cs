using System.Collections.Concurrent;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Owns the runtime element-id namespace and element lifecycle: lookup,
/// staleness detection, re-resolution via recipes, invalidation on events.
/// </summary>
public sealed class ElementRegistry
{
    private readonly ConcurrentDictionary<string, UiElement> _elements = new();
    private readonly List<IElementBackend> _backends;
    private readonly ElementRecipeStore? _store;

    public ElementRegistry(IEnumerable<IElementBackend> backends,
        ElementRecipeStore? store = null)
    { _backends = backends.ToList(); _store = store; }

    public void Register(IEnumerable<UiElement> elements)
    {
        foreach (var e in elements)
        {
            _elements[e.Id] = e;
            _store?.Put(e);
        }
    }

    public UiElement? Get(string id) => _elements.TryGetValue(id, out var e) ? e : null;

    /// <summary>Ensure the element is still usable; re-resolve once if stale.
    /// An id unknown to this process is looked up in the recipe store —
    /// ids minted by a previous one-shot process (e.g. `inbrisk find`)
    /// re-resolve through their persisted recipe.</summary>
    public UiElement? EnsureAlive(string id)
    {
        if (!_elements.TryGetValue(id, out var e))
        {
            var persisted = _store?.Lookup(id);
            if (persisted == null) return null;
            var pBackend = _backends.FirstOrDefault(b => b.Id == persisted.Backend);
            var revived = pBackend?.ReResolve(persisted);
            if (revived == null) return null;
            revived = revived with { Id = id }; // keep the caller-facing id stable
            _elements[id] = revived;
            return revived;
        }
        var backend = _backends.FirstOrDefault(b => b.Id == e.Handle.Backend);
        if (backend == null) return null;

        if (!e.IsStale && backend.IsAlive(e)) return e;

        e.IsStale = true;
        var fresh = backend.ReResolve(e.Handle);
        if (fresh == null) return null;
        fresh = fresh with { Id = e.Id }; // keep the caller-facing id stable
        _elements[e.Id] = fresh;
        return fresh;
    }

    /// <summary>Mark elements of a destroyed window stale — kept so they can
    /// re-resolve if the window reappears.</summary>
    public void InvalidateWindow(long hwnd)
    {
        foreach (var kv in _elements)
            if (kv.Value.Hwnd == hwnd || kv.Value.Handle.Recipe.Hwnd == hwnd)
                kv.Value.IsStale = true;
    }

    public int Count => _elements.Count;
}
