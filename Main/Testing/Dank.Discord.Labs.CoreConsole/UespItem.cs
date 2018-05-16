namespace Dank.Discord.Labs
{
  internal sealed class UespItem
  {
    public string Name { get; }

    public int Id { get; }

    public UespItem(string name, int id)
    {
      Name = name;
      Id = id;
    }

    public override string ToString() => Name + "=" + Id;
  }
}
