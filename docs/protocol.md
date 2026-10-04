# Protocol compatibility

The plugin speaks the Supervisor game-integration V1 WebSocket protocol. The
first frame is `connection.hello`; after `connection.accepted`, the plugin
sends registration and readiness messages, then emits lifecycle, lap, and
incident events as durable messages.

`players.snapshot` and `telemetry.snapshot` are ephemeral latest-state
observations. Telemetry is advertised only when enabled and is sampled at
1–5 Hz. Durable messages use stable IDs and retain pending acknowledgements
across reconnects within the process.

The normative schemas and reliability/security details live in the Supervisor
repository at `protocol/game-integration/v1/`. This document intentionally
does not duplicate those schemas.
