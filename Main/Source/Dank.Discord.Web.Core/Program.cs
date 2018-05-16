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
    private static Commands commands;

    public static void Main(string[] args)
    {
      commands = new Commands(DateTimeOffset.UtcNow);

      using (client = new DiscordClient(DankUserId, authToken))
      {
        using (client.Events.Subscribe(
          next => Log.Info("RECEIVED: " + next),
          ex => Log.Info(ex),
          () => Log.Info("DONE")))
        using ((from message in client.UserMessages
                let response = commands.TryProcessCommand(message.Content)
                where response != null
                from _ in client.PostMessageAsync(message.ChannelId, response)
                select Unit.Default)
                .Subscribe())
        {
          //client.Connect();

          WebHost.CreateDefaultBuilder(args)
                 .UseStartup<Startup>()
                 .Build()
                 .Run();
        }
      }
    }
  }
}
