using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Dank.Discord.Labs
{
  class Program
  {
    private static readonly string InfoFile = @"..\..\Properties\PrivateDiscordInfo.txt";
    private static readonly HttpClient Client = new HttpClient();

    static void Main(string[] args)
    {
      var response = ConnectAsync(Initialize()).Result;

      Console.WriteLine(response);
      Console.ReadKey();
    }

    private static async Task<string> ConnectAsync(ConnectionInfo connection)
    {
      var response = await Client.GetAsync(connection.Url).ConfigureAwait(false);
      var content = await response.Content.ReadAsStringAsync();

      return response.IsSuccessStatusCode ? content : "FAILED: " + response.StatusCode + Environment.NewLine + content;
    }

    private static ConnectionInfo Initialize()
    {
      var lines = File.ReadAllLines(InfoFile);
      var url = lines[0];
      var clientId = lines[1];
      var secret = lines[2];

      return new ConnectionInfo(new Uri(url), clientId, secret);
    }
  }
}
