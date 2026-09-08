using System.Runtime.InteropServices;

namespace BatteryChecker.Battery;

/// <summary>
/// Talks to the battery driver directly, the same IOCTLs HWiNFO uses.
/// Every call returns what the battery controller says right now; the
/// higher-level Windows power API caches its report and can lag by a
/// minute after you plug or unplug, which is why we don't rely on it.
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

    private static readonly Guid BatteryClass = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
    private static nint _handle = -1;
    private static uint _tag;
    private static Info _info;

    /// <summary>Live status plus static info. Null if there is no battery or the driver failed; we reopen next call.</summary>
    public static (Status Status, Info Info)? Read()
    {
        try
        {
            if (_handle == -1 && !Open())
                return null;
            uint wait = 0, tag = 0;
            if (!DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_TAG, &wait, 4, &tag, 4, out _, 0) || tag == 0)
                throw new InvalidOperationException("battery went away");
            _tag = tag;
            var ask = new BATTERY_WAIT_STATUS { BatteryTag = _tag };
            BATTERY_STATUS status;
            if (!DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_STATUS, &ask, (uint)sizeof(BATTERY_WAIT_STATUS), &status, (uint)sizeof(BATTERY_STATUS), out _, 0))
                throw new InvalidOperationException("status failed");
            return (new Status(status.PowerState, status.Capacity, status.Voltage, status.Rate), _info);
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
        foreach (var path in DevicePaths())
        {
            nint handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
            if (handle == -1)
                continue;
            uint wait = 0, tag = 0;
            if (DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, &wait, 4, &tag, 4, out _, 0) && tag != 0)
            {
                var query = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = 0 };
                BATTERY_INFORMATION info;
                if (DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, &query, (uint)sizeof(BATTERY_QUERY_INFORMATION), &info, (uint)sizeof(BATTERY_INFORMATION), out _, 0)
                    && (info.Capabilities & SystemBattery) != 0)
                {
                    _handle = handle;
                    _tag = tag;
                    _info = new Info(info.Capabilities, info.DesignedCapacity, info.FullChargedCapacity, info.CycleCount);
                    return true;
                }
            }
            CloseHandle(handle);
        }
        return false;
    }

    private static void Close()
    {
        if (_handle != -1)
            CloseHandle(_handle);
        _handle = -1;
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
                byte* buffer = stackalloc byte[(int)needed];
                *(uint*)buffer = (uint)(sizeof(nint) == 8 ? 8 : 6); // cbSize of the fixed part
                if (SetupDiGetDeviceInterfaceDetailW(devs, ref data, buffer, needed, out _, 0))
                    paths.Add(new string((char*)(buffer + 4)));
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
