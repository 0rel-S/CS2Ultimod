using CS2Ultimod.Modes.Retake.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CS2Ultimod.Modes.Retake.Managers;

public sealed class RetakeSpawnManager
{
    private readonly ILogger _logger;
    private readonly string _configDir;
    private MapSpawnConfig? _currentConfig;
    private string? _currentMap;

    public RetakeSpawnManager(ILogger logger, string configDir)
    {
        _logger = logger;
        _configDir = configDir;
    }

    public bool HasConfig => _currentConfig != null;

    public void LoadForMap(string mapName)
    {
        _currentMap = mapName;
        _currentConfig = null;

        var path = Path.Combine(_configDir, $"{mapName}.json");
        if (!File.Exists(path))
        {
            _logger.LogWarning("[Retake] No spawn config for {Map}, mode will be unavailable.", mapName);
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            _currentConfig = JsonSerializer.Deserialize<MapSpawnConfig>(json);
            var countA = _currentConfig?.A.Count ?? 0;
            var countB = _currentConfig?.B.Count ?? 0;
            _logger.LogInformation("[Retake] Loaded spawns for {Map}: A={A} B={B}", mapName, countA, countB);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retake] Failed to load spawn config for {Map}", mapName);
        }
    }

    public List<RetakeSpawn> GetSpawnsForSite(BombSite site)
        => _currentConfig?.ForSite(site) ?? [];

    public void SaveCurrentConfig()
    {
        if (_currentMap == null || _currentConfig == null) return;
        var path = Path.Combine(_configDir, $"{_currentMap}.json");
        var json = JsonSerializer.Serialize(_currentConfig, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public void AddSpawn(RetakeSpawn spawn, BombSite site)
    {
        _currentConfig ??= new MapSpawnConfig();
        _currentConfig.ForSite(site).Add(spawn);
        SaveCurrentConfig();
    }

    public void RemoveNearest(CounterStrikeSharp.API.Modules.Utils.Vector playerPos, BombSite site)
    {
        if (_currentConfig == null) return;
        var spawns = _currentConfig.ForSite(site);
        if (spawns.Count == 0) return;

        var nearest = spawns
            .OrderBy(s => VectorDistance(s.Position, playerPos))
            .First();
        spawns.Remove(nearest);
        SaveCurrentConfig();
    }

    private static float VectorDistance(CounterStrikeSharp.API.Modules.Utils.Vector a, CounterStrikeSharp.API.Modules.Utils.Vector b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));
}
