using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
namespace Telematic.AssettoServer.Plugin.Tests;

public class NetworkExposureTests
{
    private const string Token = "diagnostics-separate-credential-123456789";
    private static DefaultHttpContext Local()
    {
        var c = new DefaultHttpContext(); c.Connection.RemoteIpAddress = IPAddress.Loopback;
        c.Request.Scheme = "http"; c.Request.Host = new HostString("127.0.0.1:8084");
        c.Request.Headers["X-Telematic-Diagnostics-Token"] = Token; return c;
    }
    [Theory]
    [InlineData("ws://127.0.0.1:9780/game")]
    [InlineData("ws://[::1]:9780/game")]
    public void LocalTransportPreserved(string url) => NetworkExposure.ValidateSupervisorUrl(url);
    [Theory]
    [InlineData("ws://192.168.1.1:9780/game")]
    [InlineData("wss://public.example/game")]
    [InlineData("ws://localhost:9780/game")]
    [InlineData("ws://user:secret@127.0.0.1/game")]
    [InlineData("ws://127.0.0.1/game?token=secret")]
    public void UnsafeEndpointsFailWithoutLeakingInput(string url) => Assert.Equal("SUPERVISOR_ENDPOINT_UNSAFE", Assert.Throws<ArgumentException>(() => NetworkExposure.ValidateSupervisorUrl(url)).Message);
    [Fact] public void DiagnosticsNeedSeparateCredentialAndLocalAuthority()
    {
        var c = Local(); Assert.True(NetworkExposure.DiagnosticsAuthorized(c, Token, "plugin"));
        Assert.False(NetworkExposure.DiagnosticsAuthorized(c, "", "plugin"));
        Assert.False(NetworkExposure.DiagnosticsAuthorized(c, Token, Token));
        c.Request.Headers.Origin = "http://hostile.example"; Assert.False(NetworkExposure.DiagnosticsAuthorized(c, Token, "plugin"));
        c.Request.Headers.Origin = "http://127.0.0.1:8084"; Assert.True(NetworkExposure.DiagnosticsAuthorized(c, Token, "plugin"));
        c.Request.Host = new HostString("hostile.example"); Assert.False(NetworkExposure.DiagnosticsAuthorized(c, Token, "plugin"));
        c.Request.Host = new HostString("127.0.0.1:8084"); c.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.False(NetworkExposure.DiagnosticsAuthorized(c, Token, "plugin"));
    }
    [Fact] public void LocalShellContainsNoRuntimeStateAndPollingRequiresCredential()
    {
        var c = Local(); c.Request.Headers.Remove("X-Telematic-Diagnostics-Token");
        var controller = new TelematicController(new TelematicConfiguration { DiagnosticsToken = Token }, null!, null!) { ControllerContext = new ControllerContext { HttpContext = c } };
        Assert.IsType<UnauthorizedResult>(controller.Status()); Assert.IsType<ContentResult>(controller.Page());
        c.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.IsType<UnauthorizedResult>(controller.Status()); Assert.Equal(403, Assert.IsType<StatusCodeResult>(controller.Page()).StatusCode);
    }
    [Fact] public void AuthorizedDiagnosticsAreCredentialAndPlayerIdentifierFree()
    {
        var config = new TelematicConfiguration { DiagnosticsToken = Token, PluginToken = "private-plugin-credential" };
        using var gateway = new TelematicGatewayClient(config, new ClientWebSocketTransport());
        var events = new AssettoEventBridge(null!, null!, null!, gateway, null!);
        typeof(AssettoEventBridge).GetField("_lastLapDriver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(events, "private-player");
        typeof(AssettoEventBridge).GetField("_activeSessionId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(events, "private-session");
        var controller = new TelematicController(config, gateway, events) { ControllerContext = new ControllerContext { HttpContext = Local() } };
        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(controller.Status()).Value);
        foreach (var secret in new[] { Token, config.PluginToken, "private-player", "private-session", "LastLapDriver", "SessionId" }) Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain(Token, Assert.IsType<ContentResult>(controller.Page()).Content!);
        Assert.DoesNotContain(Token, config.ToString());
    }
    [Fact] public void WrongOrDuplicateCredentialFailsClosed()
    {
        var c = Local(); c.Request.Headers["X-Telematic-Diagnostics-Token"] = "wrong";
        Assert.False(NetworkExposure.DiagnosticsAuthorized(c, Token, "plugin"));
        c.Request.Headers["X-Telematic-Diagnostics-Token"] = new Microsoft.Extensions.Primitives.StringValues(new[] { Token, Token });
        Assert.False(NetworkExposure.DiagnosticsAuthorized(c, Token, "plugin"));
    }
}
