using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Dank.Discord.Labs
{
  class Program
  {
    private const string InnervationChannelId = "402231904063193088";
    private const string GatewayQueryString = "?v=6&encoding=json";

    private static readonly string infoFile = @"..\..\Properties\PrivateDiscordInfo.txt";
    private static readonly string authToken = File.ReadAllLines(infoFile)[0];

    private static readonly Uri baseApi = new Uri("https://discordapp.com/api/");
    private static readonly Uri gatewayBot = new Uri("gateway/bot", UriKind.Relative);

    private static readonly HttpClient client = new HttpClient();
    private static readonly ClientWebSocket socket = new ClientWebSocket();
    private static readonly byte[] buffer = new byte[2048];

    static void Main()
    {
      client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", authToken);

      var url = new Uri(baseApi, gatewayBot);
      var response = GetAsync(url, new { Url = default(string), Shards = default(int) }).Result;

      var connection = ConnectAsync(new Uri(response.Url + GatewayQueryString), new { heartbeat_interval = default(int) }).Result;

      try
      {
        Console.WriteLine(connection);
        Console.WriteLine();

        var result = SendAsync(
          DiscordOperationCode.ClientIdentity,
          new
          {
            token = authToken,
            properties = new DiscordIdentityPayload()
            {
              OperatingSystem = "windows",
              Browser = "dank",
              Device = "dank"
            },
            presence = new
            {
              since = default(string),
              game = new
              {
                name = "The Elder Scrolls Online",
                type = 0
              },
              status = "online",
              afk = false
            }
          },
          default(string))
        .Result;

        Console.WriteLine(result);
        Console.WriteLine();

        while (Console.ReadKey().Key == ConsoleKey.N)
        {
          var next = ReceiveAsync(default(string)).Result;

          Console.WriteLine(next);
        }
      }
      finally
      {
        CloseAsync().Wait();
      }

      Console.ReadKey();
    }

    private static async Task<T> GetAsync<T>(Uri url, T shape)
    {
      var response = await client.GetAsync(url.ToString()).ConfigureAwait(false);

      response.EnsureSuccessStatusCode();

      var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

      return Deserialize(json, shape);
    }

    private static async Task<T> ConnectAsync<T>(Uri url, T shape)
    {
      await socket.ConnectAsync(url, CancellationToken.None).ConfigureAwait(false);

      return await ReceiveAsync(shape).ConfigureAwait(false);
    }

    private static Task CloseAsync()
         => socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);

    private static async Task<T> ReceiveAsync<T>(T shape)
    {
      var allBuffers = Enumerable.Empty<byte>();

      WebSocketReceiveResult result = null;
      do
      {
        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), CancellationToken.None).ConfigureAwait(false);

        if ((result?.CloseStatus ?? WebSocketCloseStatus.Empty) != WebSocketCloseStatus.Empty)
        {
          throw new InvalidOperationException(result.CloseStatusDescription);
        }

        allBuffers = allBuffers.Concat(result.Count == buffer.Length ? (byte[])buffer.Clone() : buffer.Take(result.Count));
      }
      while (!result.EndOfMessage);

      return Deserialize(allBuffers.ToArray(), shape);
    }

    private static async Task<TResponse> SendAsync<TSend, TResponse>(DiscordOperationCode operationCode, TSend message, TResponse responseShape)
    {
      var json = JsonConvert.SerializeObject(
        new
        {
          op = (int)operationCode,
          d = message
        },
        Formatting.None);

      var sendBuffer = Encoding.UTF8.GetBytes(json);

      await socket.SendAsync(new ArraySegment<byte>(sendBuffer), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);

      return await ReceiveAsync(responseShape).ConfigureAwait(false);
    }

    private static T Deserialize<T>(byte[] data, T shape)
         => Deserialize(Encoding.UTF8.GetString(data), shape);

    private static T Deserialize<T>(string json, T shape)
         => shape == null || shape is string
          ? (T)(object)json
          : JsonConvert.DeserializeAnonymousType(json, shape);
  }
}
