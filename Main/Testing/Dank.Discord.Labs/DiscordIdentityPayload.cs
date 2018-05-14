using Newtonsoft.Json;

namespace Dank.Discord.Labs
{
  internal sealed class DiscordIdentityPayload
  {
    [JsonProperty("$os")]
    public string OperatingSystem { get; set; }

    [JsonProperty("$browser")]
    public string Browser { get; set; }

    [JsonProperty("$device")]
    public string Device { get; set; }
  }
}
