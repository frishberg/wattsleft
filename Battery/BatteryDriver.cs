using System.Runtime.InteropServices;

namespace BatteryChecker.Battery;

/// <summary>
/// Talks to the battery driver directly, the same IOCTLs HWiNFO uses.
/// Every call returns what the battery controller says right now; the
/// higher-level Windows power API caches its report and can lag by a
/// minute after you plug or unplug, which is why we don't rely on it.
/// A laptop with two batteries (ThinkPads with a bridge battery, Dells
/// with a slice) reads as one: capacities and rates add up.
/// </summary>
public static unsafe partial class BatteryDriver
{
    public readonly record struct Status(uint PowerState, uint CapacityMwh, uint VoltageMv, int RateMw);
    public readonly record struct Info(uint Capabilities, uint DesignMwh, uint FullMwh, uint Cycles);

    public const uint PowerOnLine = 0x1, Discharging = 0x2, Charging = 0x4, Critical = 0x8;
    public const uint UnknownCapacity = 0xFFFFFFFF, UnknownVoltage = 0xFFFFFFFF;
    public const int UnknownRate = int.MinValue;
    public const uint CapacityRelative = 0x40000000;
    private const uint SystemBattery = 0x80000000;
    private const uint ShortTerm = 0x20000000;   // a UPS: a system battery in name, but not the laptop's
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(30);   // how often to look for a battery that was just slotted in

    private static readonly Guid BatteryClass = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
    private static readonly List<(nint Handle, uint Tag, Info Info)> _batteries = new();
    private static DateTime _opened = DateTime.MinValue;

    /// <summary>How many batteries are being read, for diagnostics.</summary>
    public static int Count => _batteries.Count;

    /// <summary>Live status plus static info, summed over every battery. Null if there is none or the driver failed; we reopen next call.</summary>
    public static (Status Status, Info Info)? Read()
    {
        try
        {
            if (_batteries.Count == 0 || DateTime.UtcNow - _opened > Rescan)
            {
                Close();
                if (!Open())
                    return null;
            }

            uint powerState = 0, capabilities = 0, voltage = UnknownVoltage, cycles = 0;
            ulong capacity = 0, design = 0, full = 0;
            long rate = 0;
            bool anyCapacity = false, anyDesign = false, anyFull = false, anyRate = false;
            bool someCapacityUnknown = false, someDesignUnknown = false, someFullUnknown = false;

            for (int i = _batteries.Count - 1; i >= 0; i--)
            {
                var (handle, tag, info) = _batteries[i];
                uint wait = 0, nowTag = 0;
                if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, &wait, 4, &nowTag, 4, out _, 0) || nowTag != tag)
                {
                    // this one went away, was replaced, or Windows re-issued its tag (it does that in normal use, when the
                    // firmware revises the capacity): drop it, carry on with the rest, and look afresh on the next read
                    // so a two-battery laptop never shows half its battery for long
                    CloseHandle(handle);
                    _batteries.RemoveAt(i);
                    _opened = DateTime.MinValue;
                    continue;
                }
                var ask = new BATTERY_WAIT_STATUS { BatteryTag = tag };
                BATTERY_STATUS status;
                if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_STATUS, &ask, (uint)sizeof(BATTERY_WAIT_STATUS), &status, (uint)sizeof(BATTERY_STATUS), out _, 0))
                    throw new InvalidOperationException("status failed");

                powerState |= status.PowerState;
                capabilities |= info.Capabilities;
                if (status.Capacity != UnknownCapacity) { capacity += status.Capacity; anyCapacity = true; } else someCapacityUnknown = true;
                if (status.Rate != UnknownRate) { rate += status.Rate; anyRate = true; }
                if (voltage == UnknownVoltage) voltage = status.Voltage;
                if (info.DesignMwh != UnknownCapacity) { design += info.DesignMwh; anyDesign = true; } else someDesignUnknown = true;
                if (info.FullMwh != UnknownCapacity) { full += info.FullMwh; anyFull = true; } else someFullUnknown = true;
                cycles = Math.Max(cycles, info.Cycles);
            }
            if (_batteries.Count == 0)
                return null;

            // "Discharging" from an idle second battery must not override "charging" from the one doing the work
            if ((powerState & Charging) != 0 && rate > 0) powerState &= ~Discharging;
            if ((powerState & Discharging) != 0 && rate < 0) powerState &= ~Charging;
            // a total with one battery missing from it is not the total: unknown beats half
            if (someCapacityUnknown) anyCapacity = false;
            if (someDesignUnknown) anyDesign = false;
            if (someFullUnknown) anyFull = false;

            return (new Status(powerState, anyCapacity ? (uint)Math.Min(capacity, uint.MaxValue - 1) : UnknownCapacity, voltage, anyRate ? (int)Math.Clamp(rate, int.MinValue + 1, int.MaxValue) : UnknownRate),
                    new Info(capabilities, anyDesign ? (uint)Math.Min(design, uint.MaxValue - 1) : UnknownCapacity, anyFull ? (uint)Math.Min(full, uint.MaxValue - 1) : UnknownCapacity, cycles));
        }
        catch
        {
            Close();
            return null;
        }
    }

    /// <summary>The percent Windows shows on the taskbar, or null if unknown.</summary>
    public static double? WindowsPercent()
    {
        SYSTEM_POWER_STATUS sps;
        if (!GetSystemPowerStatus(&sps) || sps.BatteryLifePercent == 255)
            return null;
        return sps.BatteryLifePercent;
    }

    /// <summary>Windows' own "time remaining" in hours (it only computes one while on battery), or null.</summary>
    public static double? WindowsHoursLeft()
    {
        SYSTEM_POWER_STATUS sps;
        if (!GetSystemPowerStatus(&sps) || sps.BatteryLifeTime == 0xFFFFFFFF)
            return null;
        return sps.BatteryLifeTime / 3600.0;
    }

    private static bool Open()
    {
        _opened = DateTime.UtcNow;
        foreach (var path in DevicePaths())
        {
            // Read and write is the usual grant; a locked-down machine may allow read only, and the query IOCTLs need nothing more.
            nint handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
            if (handle == -1)
                handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
            if (handle == -1)
                continue;
            uint wait = 0, tag = 0;
            if (DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, &wait, 4, &tag, 4, out _, 0) && tag != 0)
            {
                var query = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = 0 };
                BATTERY_INFORMATION info;
                if (DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, &query, (uint)sizeof(BATTERY_QUERY_INFORMATION), &info, (uint)sizeof(BATTERY_INFORMATION), out _, 0)
                    && (info.Capabilities & SystemBattery) != 0 && (info.Capabilities & ShortTerm) == 0)
                {
                    _batteries.Add((handle, tag, new Info(info.Capabilities, info.DesignedCapacity, info.FullChargedCapacity, info.CycleCount)));
                    continue;
                }
            }
            CloseHandle(handle);   // an empty bay, a UPS, or a device that isn't a system battery
        }
        return _batteries.Count > 0;
    }

    private static void Close()
    {
        foreach (var (handle, _, _) in _batteries)
            CloseHandle(handle);
        _batteries.Clear();
    }

    private static List<string> DevicePaths()
    {
        var paths = new List<string>();
        nint devs = SetupDiGetClassDevsW(BatteryClass, null, 0, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == -1 || devs == 0)
            return paths;
        try
        {
            for (uint index = 0; index < 8; index++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA) };
                if (!SetupDiEnumDeviceInterfaces(devs, 0, BatteryClass, index, ref data))
                    break;
                SetupDiGetDeviceInterfaceDetailW(devs, ref data, null, 0, out uint needed, 0);
                if (needed == 0)
                    continue;
                var bytes = new byte[needed + 2];   // + a terminator's worth, in case the driver's count leaves it off
                fixed (byte* buffer = bytes)
                {
                    *(uint*)buffer = (uint)(sizeof(nint) == 8 ? 8 : 6); // cbSize of the fixed part
                    if (SetupDiGetDeviceInterfaceDetailW(devs, ref data, buffer, needed, out _, 0))
                        paths.Add(new string((char*)(buffer + 4)));
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devs);
        }
        return paths;
    }

    // ------------------------------------------------------------ win32

    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, OPEN_EXISTING = 3;
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_INFORMATION = 0x294044, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public nint Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_QUERY_INFORMATION { public uint BatteryTag; public int InformationLevel; public int AtRate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_INFORMATION
    {
        public uint Capabilities;
        public byte Technology, Reserved1, Reserved2, Reserved3;
        public uint Chemistry, DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_WAIT_STATUS { public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_STATUS { public uint PowerState, Capacity, Voltage; public int Rate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(SYSTEM_POWER_STATUS* status);

    [LibraryImport("setupapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SetupDiGetClassDevsW(in Guid classGuid, string? enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint devInfo, nint devInfoData, in Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetailW(nint devInfo, ref SP_DEVICE_INTERFACE_DATA data, byte* detail, uint detailSize, out uint required, nint devInfoData);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint devInfo);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(nint handle, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
