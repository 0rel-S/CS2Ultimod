using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Modes.Execute.Models.Enums;
using System.Runtime.InteropServices;

namespace CS2Ultimod.Modes.Execute.Models;

public sealed class Grenade
{
    [JsonConverter(typeof(GuidOrIntConverter))]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";
    public EGrenade Type { get; set; }

    [JsonConverter(typeof(VectorJsonConverter))]
    public Vector? Position { get; set; }

    [JsonConverter(typeof(QAngleJsonConverter))]
    public QAngle? Angle { get; set; }

    [JsonConverter(typeof(VectorJsonConverter))]
    public Vector? Velocity { get; set; }

    public CsTeam Team { get; set; }
    public float Delay { get; set; }

    public void Throw()
    {
        if (Position == null || Angle == null || Velocity == null)
        {
            Console.WriteLine($"[Execute] Grenade '{Name}' has null position/angle/velocity — skipping throw.");
            return;
        }

        if (Type == EGrenade.Smoke)
        {
            SmokeProjectile.Create(Position!, Angle!, Velocity!, Team);
            return;
        }

        var projectileName = Type.GetProjectileName();
        if (projectileName == null)
        {
            Console.WriteLine($"[Execute] Unknown grenade type {Type} for '{Name}'");
            return;
        }

        var entity = Utilities.CreateEntityByName<CBaseCSGrenadeProjectile>(projectileName);
        if (entity == null)
        {
            Console.WriteLine($"[Execute] Failed to create projectile entity '{projectileName}' for grenade '{Name}'.");
            return;
        }

        if (Type == EGrenade.Molotov)
            entity.SetModel("weapons/models/grenade/incendiary/weapon_incendiarygrenade.vmdl");

        entity.Elasticity = 0.33f;
        entity.IsLive = false;
        entity.DmgRadius = 350.0f;
        entity.Damage = 99.0f;
        entity.InitialPosition.X = Position.X;
        entity.InitialPosition.Y = Position.Y;
        entity.InitialPosition.Z = Position.Z;
        entity.InitialVelocity.X = Velocity.X;
        entity.InitialVelocity.Y = Velocity.Y;
        entity.InitialVelocity.Z = Velocity.Z;
        entity.Teleport(Position, Angle, Velocity);
        entity.DispatchSpawn();
        entity.Globalname = "custom";
        entity.AcceptInput("InitializeSpawnFromWorld");
    }

    public override string ToString()
        => $"Type:{Type} Pos:{Position} Angle:{Angle} Vel:{Velocity} Delay:{Delay}";
}

/// <summary>
/// Creates smoke grenade projectiles via native memory function (platform-specific signature).
/// Ported directly from bazookaCodes/cs2-executes-plugin.
/// </summary>
internal static class SmokeProjectile
{
    private static readonly string WindowsSig =
        "48 8B C4 48 89 58 ? 48 89 68 ? 48 89 70 ? 57 41 56 41 57 48 81 EC ? ? ? ? 48 8B B4 24 ? ? ? ? 4D 8B F8";

    private static readonly string LinuxSig =
        "55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 45 89 CE";

    private static readonly string Sig =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? LinuxSig : WindowsSig;

    private static readonly MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>
        CreateFunc = new(Sig);

    public static nint Create(Vector position, QAngle angle, Vector velocity, CsTeam team)
        => CreateFunc.Invoke(
            position.Handle,
            angle.Handle,
            velocity.Handle,
            velocity.Handle,
            nint.Zero,
            45,
            (byte)team);
}
