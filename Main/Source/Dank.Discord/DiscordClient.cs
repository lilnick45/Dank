using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dank.Discord
{
  public sealed class DiscordClient : IDisposable
  {
    private const string GatewayQueryString = "?v=6&encoding=json";

    private static readonly Uri baseApi = new Uri("https://discordapp.com/api/");
    private static readonly Uri gatewayBot = new Uri("gateway/bot", UriKind.Relative);
    private static readonly Func<string, Uri> channelMessages = channelId => new Uri($"channels/{channelId}/messages", UriKind.Relative);

    private readonly string botUserId;
    private readonly string authToken;

    private readonly byte[] buffer = new byte[2048];
    private readonly HttpClient client = new HttpClient();
    private ClientWebSocket socket;

    private readonly SerialDisposable connectionSubscription = new SerialDisposable();
    private readonly SerialDisposable heartbeat = new SerialDisposable();
    private readonly IScheduler sendGate = new EventLoopScheduler();
    private readonly IConnectableObservable<(object Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> events;

    private Uri gatewayAddress;
    private int lastSequenceNumber;
    private string lastSessionId;
    private DiscordConnectionState connectionState;
    private TimeSpan heartbeatInterval;
    private int retryCount;

    public IObservable<(object Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> Events => events.AsObservable();

    public IObservable<(string ChannelId, string Content)> UserMessages => from message in Events
                                                                           where message.Kind == DiscordDispatchKind.CreateMessage
                                                                           select (JObject)message.Data into json
                                                                           select new
                                                                           {
                                                                             ChannelId = json["channel_id"].ToString(),
                                                                             Content = json["content"].ToString(),
                                                                             Author = json["author"]
                                                                           }
                                                                           into messageDetails
                                                                           where messageDetails.Author["id"].ToString() != botUserId
                                                                              && !((bool?)messageDetails.Author["bot"] ?? false)
                                                                           select (messageDetails.ChannelId, messageDetails.Content);

    public DiscordClient(string botUserId, string authToken)
    {
      this.botUserId = botUserId;
      this.authToken = authToken;

      client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", authToken);

      events = ReceiveAllAsync(default(object)).Publish();
    }

    public void Connect() => connectionSubscription.Disposable = events.Connect();

    public void Disconnect() => connectionSubscription.Disposable = Disposable.Empty;

    public void Dispose() => connectionSubscription.Dispose();

    public Task<string> PostMessageAsync(string channelId, string message)
         => PostAsync(new Uri(baseApi, channelMessages(channelId)), message, default(string));

    private async Task EnsureConnectedWithSessionAsync()
    {
      switch (connectionState)
      {
        case DiscordConnectionState.Disconnected:
          await ConnectAsync().ConfigureAwait(false);

          connectionState = DiscordConnectionState.Connected;
          goto case DiscordConnectionState.Connected;

        case DiscordConnectionState.Connected:
          var identifyResponse = await IdentifyAsync().ConfigureAwait(false);

          Log.Info(identifyResponse);

          if (identifyResponse.Code == DiscordOperationCode.InvalidSession)
          {
            var delay = TimeSpan.FromSeconds(5 * ++retryCount);

            Log.Info($"Identification received an invalid session response from the Discord server. Retrying in {delay.TotalSeconds} seconds.");

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

          Log.Info(resumeResponse);

          if (resumeResponse.Code == DiscordOperationCode.InvalidSession)
          {
            var delay = TimeSpan.FromSeconds(5 * ++retryCount);

            Log.Info($"Resume received an invalid session response from the Discord server. Retrying in {delay.TotalSeconds} seconds.");

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

    private async Task EnsureSocketReadyAsync(string closeReason = null)
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

    private async Task<(object Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> IdentifyAsync()
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

    private async Task<(string Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> ResumeAsync()
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

    private void StartHeartbeat()
    {
      if (heartbeatInterval > TimeSpan.Zero)
      {
        heartbeat.Disposable = (from _ in Observable.Interval(heartbeatInterval)
                                from __ in SendHeartbeatAsync().AsUnit()
                                select Unit.Default)
                                .Subscribe();
      }
    }

    private Task SendHeartbeatAsync()
         => SendAsync(DiscordOperationCode.Heartbeat, (int?)lastSequenceNumber);

    private void StopHeartbeat() => heartbeat.Disposable = Disposable.Empty;

    private async Task<T> GetAsync<T>(Uri url, T shape)
    {
      var response = await client.GetAsync(url.ToString()).ConfigureAwait(false);

      return await ParseResponse(response, shape).ConfigureAwait(false);
    }

    private async Task<TResponse> PostAsync<TSend, TResponse>(Uri url, TSend message, TResponse responseShape)
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

    private async Task<TResponse> ParseResponse<TResponse>(HttpResponseMessage response, TResponse responseShape)
    {
      response.EnsureSuccessStatusCode();

      var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

      return Deserialize(json, responseShape);
    }

    private async Task ConnectAsync()
    {
      await EnsureSocketReadyAsync().ConfigureAwait(false);

      var gatewayResponse = await GetAsync(new Uri(baseApi, gatewayBot), new { Url = default(string), Shards = default(int) }).ConfigureAwait(false);

      Log.Info(gatewayResponse);

      gatewayAddress = new Uri(gatewayResponse.Url + GatewayQueryString);

      Log.Info(gatewayAddress);

      var connection = await ConnectAsync(gatewayAddress, new { heartbeat_interval = default(int) }).ConfigureAwait(false);

      Log.Info(connection);

      heartbeatInterval = TimeSpan.FromMilliseconds(connection.heartbeat_interval);

      Log.Info(heartbeatInterval);
    }

    private async Task<T> ConnectAsync<T>(Uri url, T shape)
    {
      await socket.ConnectAsync(url, CancellationToken.None).ConfigureAwait(false);

      return (await ReceiveAsync(shape).ConfigureAwait(false)).Data;
    }

    private async Task<(T Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> ReceiveAsync<T>(T shape, CancellationToken cancel = default(CancellationToken))
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

    private DiscordDispatchKind MapDiscordDispatchKind(string t)
    {
      switch (t)
      {
        case "MESSAGE_CREATE":
          return DiscordDispatchKind.CreateMessage;
        default:
          return DiscordDispatchKind.Unknown;
      }
    }

    private IObservable<(T Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> ReceiveAllAsync<T>(T shape)
         => Observable.Create<(T, DiscordOperationCode, DiscordDispatchKind)>((observer, cancel) => ReceiveAllAsync(shape, observer, cancel));

    private async Task ReceiveAllAsync<T>(T shape, IObserver<(T Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> observer, CancellationToken cancel = default(CancellationToken))
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
    private async Task<(TResponse Data, DiscordOperationCode Code, DiscordDispatchKind Kind)> SendAndReceiveAsync<TSend, TResponse>(DiscordOperationCode operationCode, TSend message, TResponse responseShape)
    {
      await SendAsync(operationCode, message).ConfigureAwait(false);

      return await ReceiveAsync(responseShape).ConfigureAwait(false);
    }

    private Task SendAsync<TSend>(DiscordOperationCode operationCode, TSend message)
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

    private T Deserialize<T>(byte[] data, T shape)
         => Deserialize(Encoding.UTF8.GetString(data), shape);

    private T Deserialize<T>(string json, T shape)
         => shape == null || shape is string
          ? (T)(object)json
          : JsonConvert.DeserializeAnonymousType(json, shape);
  }
}
