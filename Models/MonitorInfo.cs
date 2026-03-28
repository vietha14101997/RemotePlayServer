#nullable enable
namespace RemotePlayServer.Models;

public class MonitorInfo
{
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsVirtual { get; init; }
}
