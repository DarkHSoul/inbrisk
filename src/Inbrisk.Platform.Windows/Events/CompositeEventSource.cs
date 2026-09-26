using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Events;

/// <summary>Merges WinEvents and UIA events into one stream.</summary>
public sealed class CompositeEventSource : IEventSource
{
    private readonly List<IEventSource> _sources;

    public CompositeEventSource(params IEventSource[] sources) => _sources = sources.ToList();

    public event Action<ObservedEvent>? Event
    {
        add { foreach (var s in _sources) s.Event += value; }
        remove { foreach (var s in _sources) s.Event -= value; }
    }

    public void Start() { foreach (var s in _sources) s.Start(); }

    public void Dispose() { foreach (var s in _sources) s.Dispose(); }
}
