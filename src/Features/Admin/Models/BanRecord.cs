namespace CS2Ultimod.Features.Admin.Models;

public sealed class BanRecord
{
    public int Id { get; set; }
    public string SteamId { get; set; } = "";
    public string? Ip { get; set; }
    public string AdminId { get; set; } = "";
    public string Reason { get; set; } = "";
    public int Duration { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? ExpiresAt { get; set; }
    public int Unbanned { get; set; }
    public string? UnbanBy { get; set; }

    // DB column aliases (snake_case)
    public string steam_id { get => SteamId; set => SteamId = value; }
    public string? ip { get => Ip; set => Ip = value; }
    public string admin_id { get => AdminId; set => AdminId = value; }
    public string reason { get => Reason; set => Reason = value; }
    public int duration { get => Duration; set => Duration = value; }
    public string created_at { get => CreatedAt; set => CreatedAt = value; }
    public string? expires_at { get => ExpiresAt; set => ExpiresAt = value; }
    public int unbanned { get => Unbanned; set => Unbanned = value; }
    public string? unban_by { get => UnbanBy; set => UnbanBy = value; }
}
