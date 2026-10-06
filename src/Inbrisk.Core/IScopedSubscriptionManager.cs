namespace Inbrisk.Core;

[Flags]
public enum UiaEventKinds
{
    None = 0,
    StructureChanged = 1 << 0,
    PropertyChanged = 1 << 1,
    Notification = 1 << 2,
    LiveRegion = 1 << 3,
    WindowOpened = 1 << 4,
    WindowClosed = 1 << 5,
    FocusChanged = 1 << 6,
    All = ~0
}

public sealed record SubscriptionKey
{
    public long Hwnd { get; init; }
    public int? Pid { get; init; }
    public UiaEventKinds EventKinds { get; init; }
    public string? PropertySet { get; init; }
    public int Scope { get; init; }

    public SubscriptionKey(long Hwnd, int? Pid, UiaEventKinds EventKinds, string? PropertySet = null, int Scope = 4)
    {
        this.Hwnd = Hwnd;
        this.Pid = Pid;
        this.EventKinds = EventKinds;
        this.PropertySet = Canonicalize(PropertySet);
        this.Scope = Scope;
    }

    private static string? Canonicalize(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        if (p.Equals("default", StringComparison.OrdinalIgnoreCase)) return "default";
        var parts = p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var intList = new List<int>();
        var strList = new List<string>();
        foreach (var part in parts)
        {
            if (int.TryParse(part, out var id)) intList.Add(id);
            else strList.Add(part);
        }
        if (intList.Count > 0 && strList.Count == 0)
            return string.Join(",", intList.Distinct().OrderBy(x => x));
        return string.Join(",", parts.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }
}

public interface ISubscriptionLease : IDisposable
{
    SubscriptionKey Key { get; }
    long LeaseId { get; }
    long GenerationBaseline { get; }
    bool IsActive { get; }
}

public interface IScopedSubscriptionManager : IDisposable
{
    ISubscriptionLease Acquire(long hwnd, int? pid, UiaEventKinds kinds, int[]? propertyIds = null, int scope = 4);
    ScopedSubscriptionTelemetry Telemetry { get; }
}
