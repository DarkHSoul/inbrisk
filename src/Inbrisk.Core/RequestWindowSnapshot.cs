using System;
using System.Collections.Generic;

namespace Inbrisk.Core;

/// <summary>
/// Scoped exclusively to a single MCP tool call.
/// Caches ListWindows() results within that single tool turn so repeated internal callers
/// (e.g. blockers, bounds, tags, verification) reuse the snapshot without re-enumerating.
/// Discarded immediately after the tool turn finishes.
/// Automatically refreshes if an in-request window mutation changes MutationVersion.
/// </summary>
public sealed class RequestWindowSnapshot
{
    private readonly Func<IReadOnlyList<WindowInfo>> _windowEnumerator;
    private readonly Func<long> _mutationVersionProvider;
    private IReadOnlyList<WindowInfo>? _cached;
    private long _capturedMutationVersion;
    private int _enumerationCount;

    public int EnumerationCount => _enumerationCount;

    public RequestWindowSnapshot(
        Func<IReadOnlyList<WindowInfo>> windowEnumerator,
        Func<long> mutationVersionProvider)
    {
        _windowEnumerator = windowEnumerator ?? throw new ArgumentNullException(nameof(windowEnumerator));
        _mutationVersionProvider = mutationVersionProvider ?? throw new ArgumentNullException(nameof(mutationVersionProvider));
    }

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        var currentVersion = _mutationVersionProvider();
        if (_cached == null || currentVersion != _capturedMutationVersion)
        {
            _cached = _windowEnumerator();
            _capturedMutationVersion = currentVersion;
            _enumerationCount++;
        }
        return _cached;
    }

    public void Invalidate()
    {
        _cached = null;
    }
}
