using System.Runtime.InteropServices;

namespace BatteryChecker.Battery;

/// <summary>
/// The processor's own power meter, read through Windows' "Energy Meter" performance counters.
/// Windows 11 exposes the CPU's RAPL energy counters this way on Intel and AMD machines (Windows 10
/// only on hardware with a dedicated meter, such as some Surfaces), with no driver and no admin.
/// The counters are added by their English names so a French or Japanese Windows finds them too.
/// Everything here is best effort: if the counter set is missing, or the numbers make no sense,
/// <see cref="Watts"/> is null and the app simply doesn't estimate.
/// </summary>
public static unsafe class SystemPower
{
    private const uint PDH_FMT_DOUBLE = 0x200, PDH_MORE_DATA = 0x800007D2, PDH_INVALID_DATA = 0xC0000BC6, PDH_NO_DATA = 0x800007D5;

    private static nint _query, _counter;
    private static bool _tried, _available;
    private static byte[] _buffer = new byte[4096];

    /// <summary>What the counters were found to contain, for diagnostics.</summary>
    public static string Description { get; private set; } = "not read yet";

    /// <summary>True once the counter set has been found on this machine.</summary>
    public static bool Available => _available;

    /// <summary>
    /// The processor package's draw right now in watts (cores, integrated graphics, memory controller, plus
    /// memory where it's metered), or null if this machine doesn't expose it. Call it steadily, about four
    /// times a second: the value is the energy used since the previous call divided by the time between.
    /// </summary>
    public static double? Watts()
    {
        try
        {
            if (!_tried && !Open())
                return null;
            if (!_available)
                return null;

            uint rc = PdhCollectQueryData(_query);
            if (rc != 0)
                return null;
            uint size = (uint)_buffer.Length, count;
            fixed (byte* p = _buffer)
                rc = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out count, p);
            if (rc == PDH_MORE_DATA)
            {
                _buffer = new byte[size + 256];
                fixed (byte* p = _buffer)
                    rc = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out count, p);
            }
            if (rc == PDH_INVALID_DATA || rc == PDH_NO_DATA)
                return null;   // the first sample after opening: a rate needs two
            if (rc != 0)
                return null;

            // Each item: a pointer to the instance name, then a status and a double.
            double psys = 0, pkg = 0, dram = 0, other = 0;
            bool anyPsys = false, anyPkg = false, anyOther = false;
            var names = new List<string>();
            fixed (byte* p = _buffer)
            {
                for (uint i = 0; i < count; i++)
                {
                    byte* item = p + i * 24;
                    string name = Marshal.PtrToStringUni(*(nint*)item) ?? "";
                    uint status = *(uint*)(item + 8);
                    double value = *(double*)(item + 16);
                    if (status != 0 || double.IsNaN(value) || value < 0)
                        continue;
                    string n = name.ToLowerInvariant();
                    names.Add($"{name}={value:0}");
                    if (n == "_total") continue;
                    if (n.Contains("psys") || n.Contains("platform")) { psys += value; anyPsys = true; }
                    else if (n.EndsWith("_pkg") || n.Contains("package") && !(n.Contains("pp0") || n.Contains("pp1") || n.Contains("dram") || n.Contains("core") || n.Contains("gpu") || n.Contains("uncore"))) { pkg += value; anyPkg = true; }
                    else if (n.Contains("dram")) { dram += value; }
                    else if (!(n.Contains("pp0") || n.Contains("pp1") || n.Contains("core") || n.Contains("gpu") || n.Contains("uncore"))) { other += value; anyOther = true; }
                }
            }
            // Prefer a whole-platform meter if there is one; else the package (which already contains the cores and
            // integrated graphics) plus memory; else, on a machine with names we don't know, everything that isn't a sub-domain.
            double milliwatts = anyPsys ? psys : anyPkg ? pkg + dram : anyOther ? other + dram : -1;
            Description = $"{count} instances [{string.Join(", ", names)}] → {(anyPsys ? "platform" : anyPkg ? "package+dram" : anyOther ? "sum" : "none")}";
            if (milliwatts < 0)
                return null;
            double watts = milliwatts / 1000.0;
            return watts is > 0.05 and < 400 ? watts : null;   // outside that it isn't a laptop processor reading, whatever the units were
        }
        catch
        {
            return null;
        }
    }

    private static bool Open()
    {
        _tried = true;
        try
        {
            if (PdhOpenQueryW(null, 0, out _query) != 0)
                return false;
            if (PdhAddEnglishCounterW(_query, @"\Energy Meter(*)\Power", 0, out _counter) != 0)
            {
                Description = "no Energy Meter counter set on this machine";
                PdhCloseQuery(_query);
                _query = 0;
                return false;
            }
            _available = true;
            return true;
        }
        catch
        {
            Description = "PDH unavailable";
            return false;
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, nint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
