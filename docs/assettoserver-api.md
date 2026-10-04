# AssettoServer API compatibility

The plugin targets AssettoServer commit
`0a72890af1f8448f9fe97fb70bc1c4e7d74aca3a`.

## Hooks used

- `EntryCarManager.ClientConnected` and `ClientDisconnected` for lifecycle.
- `SessionManager.SessionChanged` for native session transitions.
- `ACTcpClient.LapCompleted` and `SectorSplit` for lap data.
- `ACTcpClient.Collision` for car and environment incidents.
- `EntryCar.PositionUpdateReceived` for opt-in sampled telemetry.
- `EntryCar.Status.CurrentTyreCompound` for the current tyre compound.

Track and configuration values are read from the server configuration without
rewriting native keys. Steam IDs are serialized as strings. When Steam auth is
disabled, the observed game GUID remains unverified and is not treated as a
trusted identity.

## Compatibility caveats

AssettoServer does not expose an authoritative session-end event in the tested
API, so the plugin does not manufacture one. Collision callbacks do not carry
a global incident ID or upstream timestamp; the plugin uses callback capture
time and a short local deduplication window for paired car callbacks. Velocity
units and some steering-related fields are not documented by the upstream API,
so the plugin preserves raw values rather than guessing units.
