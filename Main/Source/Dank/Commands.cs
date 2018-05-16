using System;

namespace Dank
{
  public class Commands
  {
    private readonly DateTimeOffset started;

    public Commands(DateTimeOffset started)
    {
      this.started = started;
    }

    public string TryProcessCommand(string content)
    {
      if (!string.IsNullOrEmpty(content))
      {
        var parts = content.Split();

        if (parts.Length >= 2 && parts[0] == "/dank")
        {
          var command = parts[1];

          switch (command)
          {
            case "/uptime":
              return "The **Dank** service has been running for " + Math.Round((DateTimeOffset.UtcNow - started).TotalMinutes, 2) + " minutes.";
            case "/help":
            case "help":
              return DiscordFixedWidthFont("/help or help :: Responds with this help message." + Environment.NewLine
                                         + "/uptime       :: Responds with the Dank service uptime.");
            default:
              return "Don't worry, **Dank** commands are coming soon!";
          }
        }
      }

      return null;
    }

    private static string DiscordFixedWidthFont(string message)
         => "```" + Environment.NewLine + message + Environment.NewLine + "```";
  }
}
