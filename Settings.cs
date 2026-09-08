using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Storage;

namespace BatteryChecker;

/// <summary>
/// Remembered window position, the toggles, and the "start with Windows" switch.
///
/// Two homes, same API:
///  - Packaged (Store): ApplicationData settings + the manifest StartupTask.
///  - Unpackaged (.exe download): a JSON file in %LocalAppData%\WattsLeft + the
///    classic HKCU Run key (also shows up in Task Manager > Startup).
/// </summary>
public static class Settings
{
    private const string StartupTaskId = "WattsLeftStartup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "WattsLeft";

    /// <summary>True when running with package identity (installed from the Store).</summary>
    public static readonly bool IsPackaged = HasPackageIdentity();

    /// <summary>Folder for our files: LocalState when packaged, %LocalAppData%\WattsLeft otherwise.</summary>
    public static readonly string DataFolder = IsPackaged
        ? ApplicationData.Current.LocalFolder.Path
        : Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WattsLeft")).FullName;

    public static (int X, int Y)? WindowPosition
    {
        get => Get("x") is int x && Get("y") is int y ? (x, y) : null;
        set
        {
            if (value is { } p) { Set("x", p.X); Set("y", p.Y); }
        }
    }

    public static bool PartyMode
    {
        get => Get("party") is true;
        set => Set("party", value);
    }

    public static bool ShowStats
    {
        get => Get("stats") is true;
        set => Set("stats", value);
    }

    public static bool ShowGraphs
    {
        get => Get("graphsOn") is true;   // off by default
        set => Set("graphsOn", value);
    }

    public static async Task<bool> StartsWithWindowsAsync()
    {
        if (!IsPackaged)
            return Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunName) is string;
        var task = await StartupTask.GetAsync(StartupTaskId);
        return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    /// <summary>Returns the state after the request. Windows may refuse if the user disabled it in Settings.</summary>
    public static async Task<bool> SetStartsWithWindowsAsync(bool enabled)
    {
        if (!IsPackaged)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --startup");
            else
                key.DeleteValue(RunName, throwOnMissingValue: false);
            return await StartsWithWindowsAsync();
        }
        var task = await StartupTask.GetAsync(StartupTaskId);
        if (enabled)
            await task.RequestEnableAsync();
        else
            task.Disable();
        return await StartsWithWindowsAsync();
    }

    // ---- storage -------------------------------------------------------------

    private static object? Get(string key)
    {
        if (IsPackaged)
            return ApplicationData.Current.LocalSettings.Values[key];
        return Json.TryGetValue(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetInt32(),
            _ => null,
        } : null;
    }

    private static void Set(string key, object value)
    {
        if (IsPackaged) { ApplicationData.Current.LocalSettings.Values[key] = value; return; }
        Json[key] = JsonSerializer.SerializeToElement(value, value is bool ? SettingsJson.Default.Boolean : SettingsJson.Default.Int32);
        try { File.WriteAllText(JsonFile, JsonSerializer.Serialize(Json, SettingsJson.Default.DictionaryStringJsonElement)); } catch { }
    }

    private static readonly string JsonFile = Path.Combine(DataFolder, "settings.json");
    private static readonly Dictionary<string, JsonElement> Json = LoadJson();

    private static Dictionary<string, JsonElement> LoadJson()
    {
        if (IsPackaged) return new();
        try
        {
            if (File.Exists(JsonFile))
                return JsonSerializer.Deserialize(File.ReadAllText(JsonFile), SettingsJson.Default.DictionaryStringJsonElement) ?? new();
        }
        catch { }
        return new();
    }

    private static bool HasPackageIdentity()
    {
        int length = 0;
        int rc = GetCurrentPackageFullName(ref length, null);
        return rc != 15700;   // APPMODEL_ERROR_NO_PACKAGE
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
internal partial class SettingsJson : System.Text.Json.Serialization.JsonSerializerContext { }
