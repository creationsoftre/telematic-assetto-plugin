using System.Net;
namespace Telematic.AssettoServer.Plugin;

// Kept independent of the HTTP host so base-.NET source transport qualification
// exercises the same production policy, without adding runtime dependencies.
public static class NativeTransportPolicy
{
    public static void ValidateSupervisorUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("ws" or "wss") || !LoopbackHost(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("SUPERVISOR_ENDPOINT_UNSAFE");
    }
    public static bool LoopbackHost(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
}
