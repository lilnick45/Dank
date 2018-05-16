using System.Reactive;
using System.Threading.Tasks;

namespace Dank.Discord
{
  internal static class TaskExtensions2
  {
    public static async Task<Unit> AsUnit(this Task task)
    {
      await task.ConfigureAwait(false);
      return Unit.Default;
    }
  }
}
