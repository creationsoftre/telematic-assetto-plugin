# AssettoServer API compatibility

The plugin targets AssettoServer v0.0.54, commit
`51737e2c2ee892bc7800df479517eaaf38a828fc`.

## Hooks used

- `EntryCarManager.ClientConnected` and `ClientDisconnected` for lifecycle.
- `ACTcpClient.Disconnecting` for early presence removal while native final
  slot cleanup is pending. Normal leave, kick and abrupt TCP cleanup converge
  on the existing native disconnect path; no synthetic lifecycle event is added.
- `SessionManager.SessionChanged` for native session transitions.
- `ACTcpClient.LapCompleted` for lap data. This release does not expose sector
  splits through a plugin callback, so sector times are unavailable.
- `ACTcpClient.Collision` for car and environment incidents.
- `EntryCar.PositionUpdateReceived` for opt-in sampled telemetry.
- `EntryCar.Status.CurrentTyreCompound` for the current tyre compound.

Track and configuration values are read from the server configuration without
rewriting native keys. Steam IDs are serialized as strings. When Steam auth is
disabled, the observed game GUID remains unverified and is not treated as a
trusted identity.

Player snapshots are emitted on ready, slot assignment, disconnect start and
final slot removal, independent of the optional telemetry sampler. Snapshot
capture and latest-state enqueue share the bridge lifecycle lock; current slot
owner references are rechecked, ambiguous Steam/slot identities are omitted and
capture timestamps strictly increase within the bridge lifetime. Obsolete
disconnect callbacks cannot remove a replacement's presence/cache. The gateway
refreshes ready state before draining cached snapshots after reconnect.

`players.snapshot` remains ephemeral/coalesced, not durable history. Existing
driver-connected/disconnected events remain durable and preserve their original
identity on replay. These source-component contracts do not certify real player,
Steam authentication, abrupt network or long-running native-host behavior.

## Compatibility caveats

AssettoServer does not expose an authoritative session-end event in the tested
API, so the plugin does not manufacture one. Collision callbacks do not carry
a global incident ID or upstream timestamp; the plugin uses callback capture
time and a short local deduplication window for paired car callbacks. Velocity
units and some steering-related fields are not documented by the upstream API,
so the plugin preserves raw values rather than guessing units.
