# Telematic AssettoServer Plugin

`Telematic.AssettoServer.Plugin` connects AssettoServer to the local Telematic
EVO Supervisor gateway. It publishes durable driver, session, lap, and
collision events, current player snapshots, and optional sampled telemetry.

## Compatibility and requirements

- .NET 11 SDK and the matching AssettoServer build.
- AssettoServer source target tested during development: commit
  `0a72890af1f8448f9fe97fb70bc1c4e7d74aca3a`.
- A configured Telematic Supervisor gateway on loopback.

Compatibility is conservative: use the tested upstream commit or verify the
AssettoServer API surface before upgrading.

## Configure and install

Copy `plugin_telematic_cfg.reference.yml` to
`plugins/Telematic.AssettoServer.Plugin/plugin_telematic_cfg.yml`. Set the
Supervisor URL, the per-instance plugin token provisioned by Supervisor, and
enable the plugin. Keep the token private and never commit the generated file.

Build with the same SDK and AssettoServer checkout used by the server, then
copy the resulting plugin directory into AssettoServer's `plugins` directory.
When using the working-directory plugin folder, start AssettoServer with
`--plugins-from-workdir`.

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
sends `instance.register` and `instance.ready`. It reconnects with bounded
backoff and replays pending durable messages by stable message ID. A process
restart does not recover unsent in-memory messages; Supervisor's outbox is the
local durability boundary.

The plugin does not implement kick, ban, or session-control commands. The
supported command is `chat.broadcast`. See `docs/` for development, protocol,
and upstream API notes.

## Troubleshooting

If the plugin cannot connect, confirm Supervisor gateway enablement, loopback
address, instance registration, token, and protocol compatibility. Check that
the plugin and AssettoServer target the same upstream API version. If laps or
incidents are absent, verify that the client is connected and that the
corresponding AssettoServer callbacks are available. Steam identity is only
trusted when Steam authentication succeeds.

## License

AssettoServer is AGPL-3.0. This plugin links against its source project and
must be distributed consistently with the upstream license and plugin terms.
