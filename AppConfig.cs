using System.IO;
using System.Text.Json;

namespace Crisol;

public class DeviceConfig
{
    public string Id { get; set; } = "";
    public double Volume { get; set; } = 100;
    public bool Enabled { get; set; }
}

public class AppConfig
{
    public double MasterVolume { get; set; } = 100;
    public bool TrayTipShown { get; set; }
    public List<DeviceConfig> Sources { get; set; } = new();
    public List<DeviceConfig> Outputs { get; set; } = new();

    public bool IsFirstRun { get; private set; }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crisol");

    private static string FilePath => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig { IsFirstRun = true };
        }
        catch { }
        return new AppConfig { IsFirstRun = true };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* config no crítica: no tumbar la app por un fallo de disco */ }
    }

    public static DeviceConfig GetOrAdd(List<DeviceConfig> list, string id)
    {
        var d = list.FirstOrDefault(x => x.Id == id);
        if (d == null)
        {
            d = new DeviceConfig { Id = id };
            list.Add(d);
        }
        return d;
    }
}
