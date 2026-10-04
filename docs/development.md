# Plugin development

The project is a .NET 11 class library loaded by AssettoServer. Project
references follow the upstream `Plugin.props` pattern and expect an
`AssettoServer` source checkout beside this repository, containing both
`AssettoServer/AssettoServer.csproj` and
`AssettoServer.Shared/AssettoServer.Shared.csproj`. If the checkout is
elsewhere, pass `-p:AssettoServerSourceRoot=<checkout path>` to `dotnet build`.

Build and test with the SDK used by the target server. The current integration
was validated against AssettoServer commit
`0a72890af1f8448f9fe97fb70bc1c4e7d74aca3a`; .NET 11 is required to compile it.

Gameplay callbacks copy immutable values and perform a non-blocking bounded
channel write. A single sender serializes durable events. Telemetry uses a
separate latest-state slot so slow connections cannot create an unbounded
backlog.

Keep protocol DTOs aligned with Supervisor V1 and preserve string encoding for
Steam and game identifiers. Do not add cloud calls or database writes to the
plugin; those responsibilities belong to Supervisor and the web application.
