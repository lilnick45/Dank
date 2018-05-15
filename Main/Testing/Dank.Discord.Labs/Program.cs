using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dank.Discord.Labs
{
  class Program
  {
    private const string InnervationChannelId = "445719805807296532";
    private const string DankUserId = "445634069439578132";

    private static readonly string infoFile = @"..\..\Properties\PrivateDiscordInfo.txt";
    private static readonly string authToken = File.ReadAllLines(infoFile)[0];

    private const string GatewayQueryString = "?v=6&encoding=json";

    private static readonly Uri baseApi = new Uri("https://discordapp.com/api/");
    private static readonly Uri gatewayBot = new Uri("gateway/bot", UriKind.Relative);
    private static readonly Func<string, Uri> channelMessages = channelId => new Uri($"channels/{channelId}/messages", UriKind.Relative);

    private static readonly byte[] buffer = new byte[2048];
    private static readonly HttpClient client = new HttpClient();
    private static ClientWebSocket socket;

    private static readonly SerialDisposable heartbeat = new SerialDisposable();
    private static readonly IScheduler sendGate = new EventLoopScheduler();

    private static Uri gatewayAddress;
    private static int lastSequenceNumber;
    private static string lastSessionId;
    private static DiscordConnectionState connectionState;
    private static TimeSpan heartbeatInterval;
    private static int retryCount;

    static void Main()
    {
      client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", authToken);

      var messages = ReceiveAllAsync(default(object)).Publish();

      using (messages.Subscribe(
        next => Debug.WriteLine("RECEIVED: " + next),
        ex => Debug.WriteLine(ex),
        () => Debug.WriteLine("DONE")))
      using ((from message in messages
              where message.Kind == DiscordDispatchKind.CreateMessage
              select (JObject)message.Data into json
              select new
              {
                ChannelId = json["channel_id"].ToString(),
                Content = json["content"].ToString(),
                Author = json["author"]
              }
              into messageDetails
              where messageDetails.Author["id"].ToString() != DankUserId
                 && !((bool?)messageDetails.Author["bot"] ?? false)
              from _ in PostMessageAsync(messageDetails.ChannelId, "You posted: " + messageDetails.Content)
              select Unit.Default)
              .Subscribe())
      using (messages.Connect())
      {
        Console.Write("> ");

        string line = null;
        while ((line = Console.ReadLine()).Length > 0)
        {
          var reply = PostMessageAsync(InnervationChannelId, line).Result;

          Console.WriteLine("RESULT: " + reply);
          Console.WriteLine();
          Console.Write("> ");
        }
      }

      Console.ReadKey();
    }

    private static Task<string> PostMessageAsync(string channelId, string message)
         => PostAsync(new Uri(baseApi, channelMessages(channelId)), message, default(string));

    private static async Task EnsureConnectedWithSessionAsync()
    {
      switch (connectionState)
      {
        case DiscordConnectionState.Disconnected:
          await ConnectAsync().ConfigureAwait(false);

          connectionState = DiscordConnectionState.Connected;
          goto case DiscordConnectionState.Connected;

        case DiscordConnectionState.Connected:
          var identifyResponse = await IdentifyAsync().ConfigureAwait(false);

          Debug.WriteLine(identifyResponse);

          if (identifyResponse.Code == DiscordOperationCode.InvalidSession)
          {
            var delay = TimeSpan.FromSeconds(5 * ++retryCount);

            Debug.WriteLine($"Identification received an invalid session response from the Discord server. Retrying in {delay.TotalSeconds} seconds.");

            await Task.Delay(delay).ConfigureAwait(false);

            connectionState = DiscordConnectionState.Disconnected;
            goto case DiscordConnectionState.Disconnected;
          }
          else
          {
            retryCount = 0;
            connectionState = DiscordConnectionState.ConnectedWithSession;
            goto case DiscordConnectionState.ConnectedWithSession;
          }

        case DiscordConnectionState.DisconnectedWithSession:
          StopHeartbeat();

          await ConnectAsync().ConfigureAwait(false);

          var resumeResponse = await ResumeAsync().ConfigureAwait(false);

          Debug.WriteLine(resumeResponse);

          if (resumeResponse.Code == DiscordOperationCode.InvalidSession)
          {
            var delay = TimeSpan.FromSeconds(5 * ++retryCount);

            Debug.WriteLine($"Resume received an invalid session response from the Discord server. Retrying in {delay.TotalSeconds} seconds.");

            await Task.Delay(delay).ConfigureAwait(false);

            connectionState = DiscordConnectionState.Connected;
            goto case DiscordConnectionState.Connected;
          }
          else
          {
            retryCount = 0;
            connectionState = DiscordConnectionState.ConnectedWithSession;
            goto case DiscordConnectionState.ConnectedWithSession;
          }

        case DiscordConnectionState.ConnectedWithSession:
          StartHeartbeat();
          break;
      }
    }

    private static async Task EnsureSocketReadyAsync(string closeReason = null)
    {
      if (socket?.State == WebSocketState.Open)
      {
        using (socket)
        {
          await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, closeReason ?? "Done", CancellationToken.None).ConfigureAwait(false);
        }
      }

      socket = new ClientWebSocket();
    }

    private static async Task<(object Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> IdentifyAsync()
    {
      var result = await SendAndReceiveAsync(
         DiscordOperationCode.Identify,
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
         new
         {
           session_id = default(string)
         })
       .ConfigureAwait(false);

      lastSessionId = result.Data.session_id;

      return result;
    }

    private static async Task<(string Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> ResumeAsync()
         => await SendAndReceiveAsync(
              DiscordOperationCode.Resume,
              new
              {
                token = authToken,
                session_id = lastSessionId,
                seq = lastSequenceNumber
              },
              default(string))
            .ConfigureAwait(false);

    private static void StartHeartbeat()
    {
      if (heartbeatInterval > TimeSpan.Zero)
      {
        heartbeat.Disposable = (from _ in Observable.Interval(heartbeatInterval)
                                from __ in SendHeartbeatAsync().AsUnit()
                                select Unit.Default)
                                .Subscribe();
      }
    }

    private static Task SendHeartbeatAsync()
         => SendAsync(DiscordOperationCode.Heartbeat, (int?)lastSequenceNumber);

    private static void StopHeartbeat() => heartbeat.Disposable = Disposable.Empty;

    private static async Task<T> GetAsync<T>(Uri url, T shape)
    {
      var response = await client.GetAsync(url.ToString()).ConfigureAwait(false);

      return await ParseResponse(response, shape).ConfigureAwait(false);
    }

    private static async Task<TResponse> PostAsync<TSend, TResponse>(Uri url, TSend message, TResponse responseShape)
    {
      var content = new FormUrlEncodedContent(new[]
      {
        new KeyValuePair<string, string>("content",
            message == null || message is string
          ? (string)(object)message
          : JsonConvert.SerializeObject(message, Formatting.None))
      });

      var response = await client.PostAsync(url.ToString(), content).ConfigureAwait(false);

      return await ParseResponse(response, responseShape).ConfigureAwait(false);
    }

    private static async Task<TResponse> ParseResponse<TResponse>(HttpResponseMessage response, TResponse responseShape)
    {
      response.EnsureSuccessStatusCode();

      var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

      return Deserialize(json, responseShape);
    }

    private static async Task ConnectAsync()
    {
      await EnsureSocketReadyAsync().ConfigureAwait(false);

      var gatewayResponse = await GetAsync(new Uri(baseApi, gatewayBot), new { Url = default(string), Shards = default(int) }).ConfigureAwait(false);

      Debug.WriteLine(gatewayResponse);

      gatewayAddress = new Uri(gatewayResponse.Url + GatewayQueryString);

      Debug.WriteLine(gatewayAddress);

      var connection = await ConnectAsync(gatewayAddress, new { heartbeat_interval = default(int) }).ConfigureAwait(false);

      Debug.WriteLine(connection);

      heartbeatInterval = TimeSpan.FromMilliseconds(connection.heartbeat_interval);

      Debug.WriteLine(heartbeatInterval);
    }

    private static async Task<T> ConnectAsync<T>(Uri url, T shape)
    {
      await socket.ConnectAsync(url, CancellationToken.None).ConfigureAwait(false);

      return (await ReceiveAsync(shape).ConfigureAwait(false)).Data;
    }

    private static async Task<(T Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> ReceiveAsync<T>(T shape, CancellationToken cancel = default(CancellationToken))
    {
      var allBuffers = Enumerable.Empty<byte>();

      WebSocketReceiveResult result = null;
      do
      {
        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), cancel).ConfigureAwait(false);

        if ((result?.CloseStatus ?? WebSocketCloseStatus.Empty) != WebSocketCloseStatus.Empty)
        {
          throw new InvalidOperationException(result.CloseStatusDescription);
        }

        allBuffers = allBuffers.Concat(result.Count == buffer.Length ? (byte[])buffer.Clone() : buffer.Take(result.Count).ToArray());
      }
      while (!result.EndOfMessage);

      var response = Deserialize(allBuffers.ToArray(),
        new
        {
          op = default(DiscordOperationCode),
          d = shape,
          s = default(int?),
          t = default(string)
        });

      lastSequenceNumber = Math.Max(response.s ?? 0, lastSequenceNumber);

      return (response.d, response.op, MapDiscordDispatchKind(response.t));
    }

    private static DiscordDispatchKind MapDiscordDispatchKind(string t)
    {
      switch (t)
      {
        case "MESSAGE_CREATE":
          return DiscordDispatchKind.CreateMessage;
        default:
          return DiscordDispatchKind.Unknown;
      }
    }

    private static IObservable<(T Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> ReceiveAllAsync<T>(T shape)
         => Observable.Create<(T, DiscordOperationCode, DiscordDispatchKind)>((observer, cancel) => ReceiveAllAsync(shape, observer, cancel));

    private static async Task ReceiveAllAsync<T>(T shape, IObserver<(T Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> observer, CancellationToken cancel = default(CancellationToken))
    {
      if (!cancel.IsCancellationRequested)
      {
        await EnsureConnectedWithSessionAsync().ConfigureAwait(false);
      }

      while (!cancel.IsCancellationRequested)
      {
        var payload = default((T Data, DiscordOperationCode Code, DiscordDispatchKind Kind));
        try
        {
          payload = await ReceiveAsync(shape, cancel).ConfigureAwait(false);

          switch (payload.Code)
          {
            case DiscordOperationCode.InvalidSession:
              connectionState = DiscordConnectionState.Connected;

              await EnsureConnectedWithSessionAsync().ConfigureAwait(false);
              break;
            case DiscordOperationCode.Reconnect:
              connectionState = DiscordConnectionState.Disconnected;

              await EnsureConnectedWithSessionAsync().ConfigureAwait(false);
              break;
            case DiscordOperationCode.Heartbeat:
              await SendHeartbeatAsync().ConfigureAwait(false);
              break;
          }
        }
        catch (Exception ex)
        {
          var cancelled = ex is OperationCanceledException || cancel.IsCancellationRequested;

          try
          {
            if (!cancelled)
            {
              connectionState = DiscordConnectionState.DisconnectedWithSession;

              await EnsureConnectedWithSessionAsync().ConfigureAwait(false);
            }
          }
          catch (Exception innerEx)
          {
            observer.OnError(new AggregateException(ex, innerEx));
            return;
          }

          continue;
        }

        observer.OnNext(payload);
      }

      if (!cancel.IsCancellationRequested)
      {
        observer.OnCompleted();
      }
    }

    /// <summary>
    /// WARNING: This method must ONLY be called from within the <see cref="EnsureConnectedWithSessionAsync"/> method, or the methods that it directly calls.
    /// The reason is because the <see cref="ReceiveAllAsync"/> method reads messages in a loop, thus we wouldn't want messages to be read twice!
    /// Overlapping reads aren't supported by the ClientWebSocket API. The <see cref="EnsureConnectedWithSessionAsync"/> method sends and receives serially, and the 
    /// <see cref="ReceiveAllAsync"/> method awaits its result before continuing to read in a loop.
    /// </summary>
    private static async Task<(TResponse Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> SendAndReceiveAsync<TSend, TResponse>(DiscordOperationCode operationCode, TSend message, TResponse responseShape)
    {
      await SendAsync(operationCode, message).ConfigureAwait(false);

      return await ReceiveAsync(responseShape).ConfigureAwait(false);
    }

    private static Task SendAsync<TSend>(DiscordOperationCode operationCode, TSend message)
    {
      var json = JsonConvert.SerializeObject(
        new
        {
          op = (int)operationCode,
          d = message
        },
        Formatting.None);

      var sendBuffer = Encoding.UTF8.GetBytes(json);

      var source = new TaskCompletionSource<Unit>();

      sendGate.Schedule(
        sendBuffer,
        (_, state) =>
        {
          try
          {
            socket.SendAsync(new ArraySegment<byte>(state), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: CancellationToken.None).Wait();
          }
          catch (Exception ex)
          {
            source.SetException(ex);
            return Disposable.Empty;
          }

          source.SetResult(Unit.Default);

          return Disposable.Empty;
        });

      return source.Task;
    }

    private static T Deserialize<T>(byte[] data, T shape)
         => Deserialize(Encoding.UTF8.GetString(data), shape);

    private static T Deserialize<T>(string json, T shape)
         => shape == null || shape is string
          ? (T)(object)json
          : JsonConvert.DeserializeAnonymousType(json, shape);
  }
}
