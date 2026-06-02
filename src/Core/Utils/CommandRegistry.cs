using CounterStrikeSharp.API.Core;

namespace CS2Ultimod.Core.Utils;

public sealed record ChatCommand(
    string Name,
    string[]? Aliases,
    string? RequiredFlag,
    string Usage,
    Action<CCSPlayerController, string[]> Handler,
    GameMode[]? AvailableInModes = null);

public interface ICommandRegistry
{
    void Register(ChatCommand cmd);
}

public sealed class CommandRegistry : ICommandRegistry
{
    // Each name maps to a list of commands — multiple entries allowed when they have
    // non-overlapping AvailableInModes (e.g. !swap in Admin vs !swap in Pickup).
    private readonly Dictionary<string, List<ChatCommand>> _commands
        = new(StringComparer.OrdinalIgnoreCase);

    /// Flat view: for each name, the first (most-recently-registered) command.
    /// Used by !help and the admin menu to enumerate available commands.
    public IReadOnlyDictionary<string, ChatCommand> All
        => _commands.ToDictionary(kv => kv.Key, kv => kv.Value[0], StringComparer.OrdinalIgnoreCase);

    public void Register(ChatCommand cmd)
    {
        RegisterName(cmd.Name, cmd);
        foreach (var alias in cmd.Aliases ?? [])
            RegisterName(alias, cmd);
    }

    private void RegisterName(string name, ChatCommand cmd)
    {
        if (!_commands.TryGetValue(name, out var bucket))
        {
            _commands[name] = [cmd];
            return;
        }

        // Allow coexistence when the new command has specific modes and none of the
        // existing mode-specific entries overlap with it.
        // A global entry (null AvailableInModes) can coexist with mode-specific ones:
        // TryResolve will prefer the mode-specific entry for its mode(s).
        if (cmd.AvailableInModes != null)
        {
            var existingSpecificModes = bucket
                .Where(c => c.AvailableInModes != null)
                .SelectMany(c => c.AvailableInModes!)
                .ToHashSet();

            if (!cmd.AvailableInModes.Any(m => existingSpecificModes.Contains(m)))
            {
                bucket.Add(cmd);
                return;
            }
        }

        throw new InvalidOperationException(
            $"[CommandRegistry] Collision: '{name}' (from '{cmd.Name}') already registered with overlapping modes.");
    }

    /// Resolve the command for a given name in the current game mode.
    public bool TryResolve(string name, GameMode currentMode, out ChatCommand? cmd)
    {
        if (!_commands.TryGetValue(name, out var bucket))
        {
            cmd = null;
            return false;
        }

        // Prefer a mode-specific match, fall back to global (null AvailableInModes).
        cmd = bucket.FirstOrDefault(c => c.AvailableInModes != null && c.AvailableInModes.Contains(currentMode))
           ?? bucket.FirstOrDefault(c => c.AvailableInModes == null);
        return cmd != null;
    }
}
