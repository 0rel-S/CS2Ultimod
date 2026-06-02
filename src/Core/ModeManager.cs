using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Core;

public sealed record ModeContext(string MapName, GameMode? PreviousMode, string? Reason);

public interface IGameMode
{
    GameMode Mode { get; }
    Task OnEnterAsync(ModeContext ctx);
    Task OnExitAsync(ModeContext ctx);
}

public interface IModeManager
{
    GameMode Current { get; }
    GameMode Default { get; }

    event Action<GameMode, GameMode>? OnModeChanged;

    Task SwitchToAsync(GameMode mode, bool reloadMap = true, string? reason = null);

    bool IsActive(params GameMode[] modes);
}

public sealed class ModeManager : IModeManager
{
    private readonly Dictionary<GameMode, IGameMode> _modes = [];
    private readonly ILogger _logger;
    private GameMode _current = GameMode.Retake;

    public GameMode Current => _current;
    public GameMode Default => GameMode.Retake;

    public event Action<GameMode, GameMode>? OnModeChanged;

    public ModeManager(ILogger logger) => _logger = logger;

    public void Register(IGameMode mode) => _modes[mode.Mode] = mode;

    public bool IsActive(params GameMode[] modes) => modes.Contains(_current);

    public async Task SwitchToAsync(GameMode mode, bool reloadMap = true, string? reason = null)
    {
        if (mode == _current)
            return;

        var previous = _current;
        var mapName = Server.MapName;
        var ctx = new ModeContext(mapName, previous, reason);

        _logger.LogInformation("[ModeManager] Switching {From} → {To} (reason: {Reason})",
            previous, mode, reason ?? "none");

        if (_modes.TryGetValue(previous, out var prevMode))
        {
            try { await prevMode.OnExitAsync(ctx); }
            catch (Exception ex) { _logger.LogError(ex, "[ModeManager] OnExitAsync failed for {Mode}", previous); }
        }

        _current = mode;
        OnModeChanged?.Invoke(previous, mode);

        if (_modes.TryGetValue(mode, out var nextMode))
        {
            try { await nextMode.OnEnterAsync(ctx); }
            catch (Exception ex) { _logger.LogError(ex, "[ModeManager] OnEnterAsync failed for {Mode}", mode); }
        }

        // Arena charge lui-même sa map Workshop dans OnEnterAsync — un reload de la
        // map courante l'écraserait (et une map de défuse n'a pas d'arènes).
        if (reloadMap && mode != GameMode.Arena)
        {
            _logger.LogInformation("[ModeManager] Reloading map {Map} for mode {Mode}", mapName, mode);
            Server.ExecuteCommand($"map {mapName}");
        }
    }
}
