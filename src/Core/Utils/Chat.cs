using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace CS2Ultimod.Core.Utils;

public static class Chat
{
    private const string Prefix = " \x0C[CS2Ultimod]\x01 ";
    private const string ErrorColor = "\x07";
    private const string SuccessColor = "\x04";

    public static void Tell(CCSPlayerController player, string message)
        => player.PrintToChat($"{Prefix}{message}");

    public static void TellError(CCSPlayerController player, string message)
        => player.PrintToChat($"{Prefix}{ErrorColor}{message}");

    public static void TellSuccess(CCSPlayerController player, string message)
        => player.PrintToChat($"{Prefix}{SuccessColor}{message}");

    public static void Broadcast(string message)
        => Server.PrintToChatAll($"{Prefix}{message}");

    public static void BroadcastError(string message)
        => Server.PrintToChatAll($"{Prefix}{ErrorColor}{message}");

    public static void BroadcastSuccess(string message)
        => Server.PrintToChatAll($"{Prefix}{SuccessColor}{message}");

    public static void HudCenter(CCSPlayerController player, string message, float durationSec = 3f)
        => player.PrintToCenterHtml(message, (int)durationSec);

    public static void HudCenterAll(string message, float durationSec = 3f)
    {
        foreach (var p in PlayerExt.AllConnected())
            HudCenter(p, message, durationSec);
    }
}
