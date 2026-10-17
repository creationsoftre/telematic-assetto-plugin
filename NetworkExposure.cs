using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Telematic.AssettoServer.Plugin;

public static class NetworkExposure
{
    public static void ValidateSupervisorUrl(string value)
        => NativeTransportPolicy.ValidateSupervisorUrl(value);

    public static bool LocalRequest(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer) ||
            !NativeTransportPolicy.LoopbackHost(context.Request.Host.Host)) return false;
        var origins = context.Request.Headers.Origin;
        if (origins.Count == 0) return true;
        return origins.Count == 1 && Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) &&
            origin.GetLeftPart(UriPartial.Authority) == $"{context.Request.Scheme}://{context.Request.Host}" &&
            origin.AbsolutePath == "/" && origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0;
    }

    public static bool DiagnosticsAuthorized(HttpContext context, string token, string pluginToken)
    {
        if (!LocalRequest(context) || token.Length < 32 || token == pluginToken) return false;
        var supplied = context.Request.Headers["X-Telematic-Diagnostics-Token"];
        return supplied.Count == 1 && supplied[0] is { Length: <= 1024 } value &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
