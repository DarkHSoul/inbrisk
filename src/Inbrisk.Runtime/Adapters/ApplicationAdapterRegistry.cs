using System.Collections.Concurrent;
using Inbrisk.Core;

namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// Registry and dispatcher for application specialist adapters.
/// Manages adapters for media, specific applications, CLI tools, and HTTP remote controls,
/// routing semantic actions to the most reliable and fastest channel available.
/// </summary>
public sealed class ApplicationAdapterRegistry
{
    private readonly ConcurrentDictionary<string, IApplicationAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);

    public ApplicationAdapterRegistry(bool registerDefaults = true)
    {
        if (registerDefaults)
        {
            Register(new WindowsMediaAdapter());
            Register(new TestAppAdapter());
        }
    }

    public void Register(IApplicationAdapter adapter)
    {
        if (adapter == null) throw new ArgumentNullException(nameof(adapter));
        _adapters[adapter.AdapterId] = adapter;
    }

    public bool Unregister(string adapterId) => _adapters.TryRemove(adapterId, out _);

    public IApplicationAdapter? GetAdapter(string adapterId)
        => _adapters.TryGetValue(adapterId, out var a) ? a : null;

    public IReadOnlyList<IApplicationAdapter> GetAllAdapters() => _adapters.Values.ToList();

    public IReadOnlyList<AdapterCapability> GetCapabilities()
    {
        return _adapters.Values.Select(a => new AdapterCapability(
            AdapterId: a.AdapterId,
            DisplayName: a.DisplayName,
            SupportedActions: a.SupportedActions,
            IsActive: true
        )).ToList();
    }

    public IReadOnlyList<IApplicationAdapter> FindApplicable(string? processName, long? hwnd)
    {
        return _adapters.Values
            .Where(a => a.IsApplicable(processName, hwnd))
            .ToList();
    }

    /// <summary>
    /// Attempts to route an action to a specialist adapter.
    /// Returns (true, result) if an adapter handled the action, or (false, null) if no adapter applies.
    /// </summary>
    public async Task<(bool Handled, AdapterResult? Result)> TryExecuteAsync(
        string action,
        TargetRef? target = null,
        IReadOnlyDictionary<string, object?>? args = null,
        string? processName = null,
        long? hwnd = null,
        string? preferredAdapterId = null,
        CancellationToken ct = default)
    {
        // 1. If explicit preferred adapter requested, query it first
        if (!string.IsNullOrWhiteSpace(preferredAdapterId) && _adapters.TryGetValue(preferredAdapterId, out var pref))
        {
            if (pref.CanHandle(action, target, args))
            {
                var res = await pref.ExecuteAsync(action, target, args, ct);
                return (true, res);
            }
        }

        // 2. Query applicable adapters for process/hwnd
        var applicable = FindApplicable(processName, hwnd);
        foreach (var adapter in applicable)
        {
            if (adapter.CanHandle(action, target, args))
            {
                var res = await adapter.ExecuteAsync(action, target, args, ct);
                return (true, res);
            }
        }

        // 3. Query all remaining adapters for universal actions (e.g. global media keys)
        foreach (var adapter in _adapters.Values)
        {
            if (!applicable.Contains(adapter) && adapter.CanHandle(action, target, args))
            {
                var res = await adapter.ExecuteAsync(action, target, args, ct);
                return (true, res);
            }
        }

        return (false, null);
    }
}
