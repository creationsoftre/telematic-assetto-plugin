# Plugin development

The project is a .NET 8 class library loaded by AssettoServer v0.0.54. Project
references follow the upstream `Plugin.props` pattern and expect an
`AssettoServer` source checkout beside this repository, containing both
`AssettoServer/AssettoServer.csproj` and
`AssettoServer.Shared/AssettoServer.Shared.csproj`. If the checkout is
elsewhere, pass `-p:AssettoServerSourceRoot=<checkout path>` to `dotnet build`.

Build and test with the SDK used by the target server. The current integration
compiles against AssettoServer v0.0.54, commit
`51737e2c2ee892bc7800df479517eaaf38a828fc`.

Gameplay callbacks validate, serialize, and synchronously flush durable events
to the local spool before reporting capture success. This introduces disk-write
latency at the callback boundary; do not describe it as a non-blocking memory
queue. The sender loads only eligible records for its bounded window. Telemetry
uses separate coalesced latest-state slots and never enters the spool.

Keep protocol DTOs aligned with Supervisor V1 and preserve string encoding for
Steam and game identifiers. Do not add cloud calls or database writes to the
plugin; cloud upload remains the Supervisor's responsibility. The local
file-backed spool has no database/service dependency. Native handshake support
for durable ACK contract 2 is required on both ends.

For a fresh supported-target build, run from this plugin directory:

```powershell
dotnet build .\Telematic.AssettoServer.Plugin.csproj --configuration Release -t:Rebuild -p:AssettoServerSourceRoot=C:\path\to\AssettoServer-v0.0.54 -p:NO_SINGLE_FILE=1
dotnet test .\tests\Telematic.AssettoServer.Plugin.Tests.csproj --configuration Release -p:AssettoServerSourceRoot=C:\path\to\AssettoServer-v0.0.54 -p:NO_SINGLE_FILE=1
```

Use the pinned v0.0.54 checkout, not master. Tests require ASP.NET Core 8 runtime;
running them with major runtime roll-forward is supplemental evidence only,
not supported-runtime release qualification. The SDK resolver permits a newer
stable SDK but the target frameworks remain net8.0.
