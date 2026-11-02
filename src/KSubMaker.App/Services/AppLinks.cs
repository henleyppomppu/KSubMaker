using System.Diagnostics;

namespace KSubMaker.App.Services;

/// <summary>Outside links the app points to: the community Discord.</summary>
public static class AppLinks
{
    public const string Discord = "https://discord.gg/BT6ZKkBxn";

    /// <summary>The link as shown, without the scheme.</summary>
    public const string DiscordText = "discord.gg/BT6ZKkBxn";

    /// <summary>Opens a link in the default browser. A link that will not open is simply not opened.</summary>
    public static void Open(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing to do — no browser, or a policy blocks it.
        }
    }
}
