namespace Dank.Discord.Labs
{
  internal sealed class UespItem
  {
    public string Name { get; }

    public int Id { get; }

    public UespItemQuality Quality { get; }

    public UespItem(string name, int id, UespItemQuality quality)
    {
      Name = name;
      Id = id;
      Quality = quality;
    }

    public override string ToString() => Name + "=" + Id;
  }
}
