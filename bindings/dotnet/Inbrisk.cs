using System.Runtime.InteropServices;
using System.Text;

namespace Inbrisk;

/// <summary>
/// P/Invoke client for inbrisk_ffi. Each call connects to the running runtime.
/// </summary>
public static class Runtime
{
    const int Capacity = 1024 * 1024;

    [DllImport("inbrisk_ffi", CallingConvention = CallingConvention.Cdecl)]
    static extern nint inbrisk_status(byte[] buf, nuint cap);

    [DllImport("inbrisk_ffi", CallingConvention = CallingConvention.Cdecl)]
    static extern nint inbrisk_run_json(byte[] planJson, int dryRun, byte[] buf, nuint cap);

    public static string StatusJson()
    {
        var buf = new byte[Capacity];
        var n = inbrisk_status(buf, (nuint)buf.Length);
        if (n < 0) throw new InvalidOperationException("inbrisk_status failed");
        return Encoding.UTF8.GetString(buf, 0, (int)n);
    }

    public static string RunJson(string planJson, bool dryRun = false)
    {
        var plan = Encoding.UTF8.GetBytes(planJson + "\0");
        var buf = new byte[Capacity];
        var n = inbrisk_run_json(plan, dryRun ? 1 : 0, buf, (nuint)buf.Length);
        if (n < 0) throw new InvalidOperationException("inbrisk_run_json failed");
        return Encoding.UTF8.GetString(buf, 0, (int)n);
    }
}
