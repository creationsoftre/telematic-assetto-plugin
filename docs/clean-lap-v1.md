# Native clean-lap-v1 evidence and retention

The exact, executable interop fixture is
[`tests/fixtures/native-clean-lap-v1.json`](../tests/fixtures/native-clean-lap-v1.json).
It is a protocol v1 durable `lap.completed` envelope. The only gameplay wire
addition is the optional `payload.recordValidation` object. Legacy payloads
without it remain unchanged and unevaluated; no migration promotes them.

```json
{
  "policyVersion": "clean-lap-v1",
  "accepted": false,
  "reason": "TELEMETRY_INCOMPLETE",
  "evidence": {
    "source": "ASSETTO_SERVER_NATIVE",
    "coverageComplete": false,
    "identityMatched": true,
    "timingCut": false,
    "collision": false,
    "intervalStart": "2026-11-09T22:00:00.0000000+00:00",
    "intervalEnd": "2026-11-09T22:01:40.0000000+00:00"
  }
}
```

`source` is case-sensitive: **ASSETTO_SERVER_NATIVE**, with an underscore
between ASSETTO and SERVER. The existing adapter name `ASSETTOSERVER_NATIVE`
is a different field and spelling. Times are RFC3339 with UTC offsets and up
to seven fractional digits. Payload `occurredAt` and evidence `intervalEnd`
are the same captured UTC callback time, serialized with the same string.
Envelope `occurredAt`/`sentAt` describe capture/enqueue, not an upstream clock.
Native `lapTimeMs` remains the packet duration. Steam and slot IDs stay strings.

Allowed reasons are `ACCEPTED`, `TIMING_CUT`, `COLLISION`,
`TELEMETRY_INCOMPLETE`, `IDENTITY_UNBOUND`, `UNSUPPORTED_SOURCE`,
`INVALID_DURATION`, `LEGACY_NOT_EVALUATED`, and `INVALID_EVIDENCE`.
`accepted` is true only for `ACCEPTED`. Raw native `validity` stays `UNKNOWN`,
including when cuts are zero or positive. `cuts` is preserved independently.

## Current external capability gap

The supported real dependency is AssettoServer v0.0.54, commit
`51737e2c2ee892bc7800df479517eaaf38a828fc`.
Its [ACTcpClient collision callback](https://github.com/compujuckel/AssettoServer/blob/51737e2c2ee892bc7800df479517eaaf38a828fc/AssettoServer/Network/Tcp/ACTcpClient.cs)
documents delayed delivery. `CollisionEventArgs` has target, speed, position,
and relative position, but no upstream timestamp or global incident ID. No
authoritative delivery watermark proves that all collisions for a lap arrived.

Consequently **native coverageComplete is always false**. Diagnostics explicitly
report `nativeCleanLapCoverageAvailable: false` and
`nativeCleanLapCoverageGap: COLLISION_TIMESTAMP_AND_DELIVERY_WATERMARK_UNAVAILABLE`.
An otherwise clean native lap is rejected with `TELEMETRY_INCOMPLETE`.
This is an intentional capability limit, not certification of clean native laps.

Verified binding requires Steam authentication enabled, a current attached
connection with unchanged GUID/slot/car reference, matching packet slot,
and the active native session reference matching the durable session binding.
Continuity is per connection. Stop/restart, reconnect, session transition,
stale client, missing session, and identity mismatch cannot establish complete
coverage. At the first completion the interval starts at actual observation
attachment/session reset time; this is a partial observation, not a verified
start-line crossing. Consecutive completions anchor later intervals. The
plugin never back-calculates a supposedly observed start from `lapTimeMs`.
Observed elapsed time can differ from packet duration. Short/invalid intervals
must fail closed downstream as well; the fixture uses an exactly matching
duration for interoperability. Every native interval still has incomplete
collision coverage even when its timestamps span the full reported duration.

Positive native cuts set `timingCut: true` and reject bound laps with
`TIMING_CUT`. Collision callbacks are retained as independent incident events;
they do not set a lap's `collision` flag without trustworthy interval
attribution. Thus `collision: false` means no attributable collision evidence,
not proof of no collision. The independent policy rejects `COLLISION` when
trustworthy attributable evidence is supplied; this upstream API cannot supply
that evidence for an exact lap. Damage values are never collision proof.

A bounded finalization delay cannot establish which lap contains a delayed
collision, especially near boundaries/session changes/slot reuse. There is no
delayed finalizer or replay correction, and queued payloads never mutate.
Enabling complete coverage requires an upstream authoritative incident time,
stable participant identity across delivery, and a verified boundary/delivery
completeness mechanism. A callback-time heuristic is insufficient.

## Local retention and failure behavior

Each completed observation is synchronously flushed as a checksum-protected,
immutable envelope under `<instance spool>/completed-laps/<messageId>.json`
before delivery eligibility. Bound observations use `delivery: durable`.
Unbound observations use `delivery: local-only`, omit `payload.sessionId`,
preserve the observed GUID/slot, and never enter the gateway spool. This local
format is not a new gateway delivery mode. No fake session or identity is made.

Pending spool files and archive files have independent lifetimes. Positive ACK
or permanent rejection requires successful archive verification and durable
`<messageId>.json.settled` creation before pending deletion/retirement. This
settlement retains the exact checksum record and is not cloud receipt proof.
ACK loss/restart replays the original immutable payload. Settled observations
remain archived indefinitely. Complete interrupted archive temporary writes
recover on restart. Eligible archived laps missing a pending spool record are
requeued when space is available; unbound laps are never requeued. No ACK is
synthesized, and failed archival/settlement cannot retire the pending event.

Spool limits never evict archive records. Archive retention has no automatic
age/size eviction and shares available local disk space. Disk full, permissions,
corrupt bytes, settlement failures, and spool saturation surface in protected
`/telematic/status`: `gateway.spool.storageHealthy`, `errorCode`, `archiveError`,
`archivedLaps`, `archiveBytes`, `archivedAwaitingDelivery`, plus
`gateway.captureFailures`. Corrupt archive bytes remain for operator recovery;
a successful later capture does not clear recovery-corruption health.
`archiveBytes` counts completed observation records, excluding settlement copies
and unfinished/corrupt files; it is not total disk usage or a retention budget.

If the disk refuses even the first archive write, no software can promise
durable preservation of the new observation. The callback records a capture
failure and reports degraded health; it never claims success or deletes another
record to make space. Keep disk capacity monitored. Windows uses file flush
and rename for process-crash recovery; sudden power-loss durability still
depends on the filesystem/hardware. Unix additionally fsyncs directories.

## Qualification

The .NET 8 tests compile against the actual pinned AssettoServer project and
invoke its real event delegate/argument types with synthetic native slot state.
They cover raw UNKNOWN preservation, optional legacy wire shape, policy cases,
canonical interval end, source spelling, missing identity/session, immutable
late-collision behavior, persistence-before-dedup, archive/spool failures,
restart/ACK loss, and archive retention after ACK/permanent rejection.
These tests do not certify a running game server, real Steam ticket checks,
real racing incidents, or an end-to-end cloud deployment.

Run the .NET 8 suite as described in [development.md](development.md). The
standalone runtime harness also supports actual process-kill archive boundaries:

```powershell
dotnet build tests/RuntimeQualification/SpoolHost.csproj --configuration Release
dotnet tests/RuntimeQualification/bin/Release/net8.0/SpoolHost.dll C:/absolute/isolated/test-root archive
```

Use a fresh isolated root for each run. Invoke the DLL through `dotnet` as
shown so child-process arguments match the harness. It kills only its own
newly spawned fixture processes. This harness links the production spool
source; it does not load the native game host. Its archive mode covers flush,
rename, settlement start, settlement flush, and settlement rename.

For a read-only interop check against the sibling Web checkout's actual schema
and evaluator (requires a Node version supporting TypeScript stripping and the
Web checkout's installed dependencies):

```powershell
node --experimental-strip-types tests/verify-native-clean-lap-interop.mjs ../telematic-web
```
