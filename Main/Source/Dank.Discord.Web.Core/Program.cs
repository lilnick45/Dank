using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;

namespace Dank.Discord.Web.Core
{
  public class Program
  {
    private const string DankUserId = "445634069439578132";

    private static readonly string infoFile = @"$pdi";
    private static readonly string authToken = File.ReadAllLines(infoFile)[0];

    private static DiscordClient client;

    public static void Main(string[] args)
    {
      using (client = new DiscordClient(DankUserId, authToken))
      {
        using (client.Events.Subscribe(
          next => Log.Info("RECEIVED: " + next),
          ex => Log.Info(ex),
          () => Log.Info("DONE")))
        using ((from message in client.UserMessages
                let response = TryProcessCommand(message.Content)
                where response != null
                from _ in client.PostMessageAsync(message.ChannelId, response)
                select Unit.Default)
                .Subscribe())
        {
          client.Connect();

          WebHost.CreateDefaultBuilder(args)
                 .UseStartup<Startup>()
                 .Build()
                 .Run();
        }
      }
    }

    private static string TryProcessCommand(string content)
    {
      if (!string.IsNullOrEmpty(content))
      {
        var parts = content.Split();

        if (parts.Length >= 2 && parts[0] == "/dank")
        {
          var command = parts[1];

          switch (command)
          {
            case "help":
            default:
              return "Don't worry, **Dank** commands are coming soon!";
          }
        }
      }

      return null;
    }
  }
}
