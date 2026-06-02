using CS2Ultimod.Core.Database;

namespace CS2Ultimod.Modes.Execute;

/// <summary>
/// Boot-time registration for Execute mode.
/// Call ExecuteBootstrap.Register() from CS2UltimodPlugin.Load() before migrations run.
///
/// In CS2UltimodPlugin.cs Load(), add:
///   // TODO: ModeManager.Register(new ExecuteMode())  — Track B
///   ExecuteBootstrap.Register();
///
/// The ModeManager.Register call wires ExecuteMode into the mode system.
/// ExecuteBootstrap.Register() ensures the DB migration is queued before RunMigrationsAsync().
/// </summary>
public static class ExecuteBootstrap
{
    public static void Register()
    {
        DatabaseRegistry.Register(new ExecuteMigration());
    }
}
