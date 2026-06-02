using System.Text.Json;

namespace CS2Ultimod.Modes.Pickup;

/// <summary>
/// Configuration model loaded from configs/pickup.json.
/// </summary>
public sealed class PickupConfig
{
    public string FaceitApiKey { get; set; } = string.Empty;
    public List<string> MapPool { get; set; } = ["de_mirage", "de_inferno", "de_nuke", "de_anubis", "de_ancient", "de_dust2", "de_train"];
    public int ReadyTimeout { get; set; } = 300;
    public int PauseLimit { get; set; } = 1;

    public static PickupConfig Load(string pluginDirectory)
    {
        // configs/ lives 3 levels up from the plugin's ModuleDirectory (addons/counterstrikesharp/plugins/CS2Ultimod/)
        var paths = new[]
        {
            Path.Combine(pluginDirectory, "..", "..", "..", "configs", "pickup.json"),
            Path.Combine(pluginDirectory, "configs", "pickup.json"),
            Path.Combine(AppContext.BaseDirectory, "configs", "pickup.json"),
        };

        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) continue;
            try
            {
                var json = File.ReadAllText(full);
                return JsonSerializer.Deserialize<PickupConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new PickupConfig();
            }
            catch { /* fall through */ }
        }

        return new PickupConfig();
    }
}
