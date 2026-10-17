# Telematic AssettoServer Plugin

`TelematicPlugin.dll` connects AssettoServer v0.0.54 to the local Telematic
Supervisor gateway. It publishes durable driver, session, lap, and
collision events, current player snapshots, and optional sampled telemetry.

## Compatibility and requirements

- .NET 8 SDK and the AssettoServer v0.0.54 source/binary (commit
  `51737e2c2ee892bc7800df479517eaaf38a828fc`).
- A configured Telematic Supervisor gateway on loopback.

The plugin compiles against the v0.0.54 tag. Live loading and telemetry still
need to be checked on an actual v0.0.54 server before production rollout.

## Configure and install

Build against a local checkout of the v0.0.54 tag:

```powershell
dotnet build .\Telematic.AssettoServer.Plugin.csproj -p:AssettoServerSourceRoot=C:\path\to\AssettoServer-v0.0.54 -p:NO_SINGLE_FILE=1
```

Place the output DLL at `plugins/TelematicPlugin/TelematicPlugin.dll`. If an
older `Telematic.AssettoServer.Plugin` directory or `EnablePlugins` entry is
present, replace it with the new name rather than loading both versions. In
the existing first YAML document of `cfg/extra_cfg.yml`, add
`TelematicPlugin` to `EnablePlugins` (preserving unrelated entries):

```yaml
EnablePlugins:
  - TelematicPlugin
```

Append the contents of `plugin_telematic_cfg.reference.yml` as a separate YAML
document at the bottom of the *same* `cfg/extra_cfg.yml` file. Set `InstanceId`
to the Supervisor instance ID, supply its local plugin token and gateway URL,
then set `Enabled: true`. Keep the token private and never commit the edited
configuration file. v0.0.54 does not read a standalone
`plugin_telematic_cfg.yml` file.

If `plugins/` is under the working directory rather than beside the server
executable, launch with `--plugins-from-workdir`. Do not install the old
`Telematic.AssettoServer.Plugin.dll` beside the new DLL.

```yaml
Telemetry:
  Enabled: false
  SampleRateHz: 2
  StaleSeconds: 5
```

Telemetry is disabled by default. When enabled, the supported sample rate is
1–5 Hz and samples remain ephemeral.

## Behavior

The plugin authenticates with `connection.hello`, waits for acceptance, then
starts ACK reception before replaying durable events and sending
`instance.register` and `instance.ready`. Durable events are validated and
flushed to a per-instance disk spool before transmission. Restart recovery and
retries preserve the original message ID; only a positive Supervisor durable
ACK retires a record. Both plugin and Supervisor must support durable ACK
contract 2 (envelope protocol remains V1). Incompatible native versions fail
explicitly instead of silently weakening delivery guarantees.

The spool is automatically created beneath the running account's local
application-data directory, under `Telematic/plugin-state/<instance hash>/spool`.
Defaults are 100 MiB and 20,000 records per instance. Rejected and quarantined
records consume this allowance. Full/unavailable storage refuses new captures
with visible errors; it never evicts earlier competitive events. Keep the same
instance ID and service account across restarts. Do not clear pending spool
files. See [delivery reliability](docs/delivery-reliability.md) for settings,
failure boundaries, and coordinated upgrade requirements.

The plugin does not implement kick, ban, or session-control commands. The
supported command is `chat.broadcast`. See `docs/` for development, protocol,
and upstream API notes.

### Plugin diagnostics

Diagnostics are disabled unless `DiagnosticsToken` is a separate private
credential of at least 32 characters (never reuse `PluginToken`). Both
`/telematic` and `/telematic/status` require direct loopback access and a literal
loopback Host. Status requires the `X-Telematic-Diagnostics-Token` header. A supplied Origin
must match the local authority. Forwarded headers do not confer local access.
Use an authenticated local HTTP client, or open the local static login shell at
`/telematic`. That shell contains no runtime diagnostics or credentials. It
prompts for the separate token for its authenticated polling requests and holds
it in memory only. Never put credentials
in a URL, logs, shared browser profiles, or persistent browser storage.
Player names and session identifiers are not returned. AssettoServer's public
game HTTP listener is unchanged; it does not authorize plugin diagnostics.

The Supervisor gateway URL must use a literal loopback IP (`127.0.0.1` or
`[::1]`), `ws`/`wss`, and no embedded credentials, query or fragment. Remote
endpoints (including remote TLS) are unsupported and rejected rather than
silently exposing native control. No TLS/proxy exception is provisioned here.

## Troubleshooting

If the plugin cannot connect, confirm Supervisor gateway enablement, loopback
address, instance registration, token, and protocol compatibility. Check that
the plugin and AssettoServer target the same upstream API version. If the plugin
is not found, check that both its directory and DLL are named `TelematicPlugin`
and that `EnablePlugins` contains that exact name. If laps or
incidents are absent, verify that the client is connected and that the
corresponding AssettoServer callbacks are available. Steam identity is only
trusted when Steam authentication succeeds.

AssettoServer v0.0.54 does not expose sector-split callbacks to this plugin;
lap events report sector times as unavailable.

## License

AssettoServer is AGPL-3.0. This plugin links against its source project and
must be distributed consistently with the upstream license and plugin terms.
