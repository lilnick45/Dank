using System;

namespace Dank.Discord.Labs
{
  internal struct ConnectionInfo
  {
    public Uri Url { get; }

    public string ClientId { get; }

    public string Secret { get; }

    public ConnectionInfo(Uri url, string clientId, string secret)
    {
      Url = url;
      ClientId = clientId;
      Secret = secret;
    }
  }
}
