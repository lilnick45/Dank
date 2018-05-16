using System.Diagnostics;

namespace Dank
{
  public static class Log
  {
    public static readonly TraceSource Default = new TraceSource("Dank");

    public static void Info(string message) => Default.TraceInformation(message);

    public static void Info(object data) => Default.TraceData(TraceEventType.Information, 0, data);
  }
}
