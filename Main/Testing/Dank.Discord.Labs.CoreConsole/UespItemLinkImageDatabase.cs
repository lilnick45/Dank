using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dank.Discord.Labs
{
  class UespItemLinkImageDatabase
  {
    private const int DefaultLevel = 50;

    private static readonly Uri SetSummary = new Uri("http://esolog.uesp.net/exportJson.php?table=setSummary");
    private static readonly Uri BaseApiUrl = new Uri("http://esoitem.uesp.net/itemLinkImage.php");

    private static readonly StringBuilder normalizingBuilder = new StringBuilder(64);

    public UespItemLinkImageDatabase(IEnumerable<UespItem> items)
    {
      Items = items;
    }

    private IEnumerable<UespItem> Items { get; }

    private Uri GetUrl(UespItem item, int level, UespItemQuality quality)
         => new Uri(BaseApiUrl, $"?itemid={item.Id}&level={level}&quality={(int)quality}");

    public Uri FindOneItemStrict(string search, int? level = 50, UespItemQuality? quality = UespItemQuality.Default)
    {
      var items = FindItems(search, level, quality).Take(2).ToList();

      return items.Count == 1 ? items[0] : null;
    }

    public IEnumerable<Uri> FindItems(string search, int? level = 50, UespItemQuality? quality = UespItemQuality.Default)
        => from item in FindItems(search)
           select GetUrl(item, level ?? DefaultLevel, quality ?? UespItemQuality.Default);

    private IEnumerable<UespItem> FindItems(string search)
    {
      if (TryNormalizeIntoParts(search, out var parts))
      {
        foreach (var item in Items)
        {
          if (parts.All(part => item.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0))
          {
            yield return item;
          }
        }
      }
    }

    private static bool TryNormalizeIntoParts(string search, out string[] itemNameParts)
    {
      // Passing in null splits on any Unicode whitespace characters
      itemNameParts = search?.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

      if (itemNameParts?.Length > 0)
      {
        for (var i = 0; i < itemNameParts.Length; i++)
        {
          itemNameParts[i] = Normalize(itemNameParts[i]);
        }

        return true;
      }

      return false;
    }

    /// <summary>
    /// Removes non-letter characters from the specified string, such as apostrophes and dashes.
    /// </summary>
    private static string Normalize(string itemName)
    {
      if (string.IsNullOrEmpty(itemName))
      {
        return itemName;
      }

      normalizingBuilder.Append(itemName);

      for (var i = 0; i < normalizingBuilder.Length; i++)
      {
        if (!char.IsLetter(normalizingBuilder[i]))
        {
          normalizingBuilder.Remove(i, 1);
          i--;
        }
      }

      var result = normalizingBuilder.ToString();

      normalizingBuilder.Clear();

      return result;
    }
  }
}
