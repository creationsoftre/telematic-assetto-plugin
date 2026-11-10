// Optional read-only integration against the sibling Web worker's real policy.
// node --experimental-strip-types tests/verify-native-clean-lap-interop.mjs ../telematic-web
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const webRoot = process.argv[2];
assert.ok(webRoot, "Pass the Web checkout path");
const { evaluateLapRecord, recordValidationSchema } = await import(pathToFileURL(
  path.resolve(webRoot, "src/lib/racing/lap-record-policy.ts")).href);
const fixture = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)),
  "fixtures/native-clean-lap-v1.json"), "utf8"));
const payload = fixture.payload;
const original = JSON.stringify(payload);
assert.ok(recordValidationSchema.safeParse(payload.recordValidation).success);
assert.equal(payload.recordValidation.evidence.source, "ASSETTO_SERVER_NATIVE");
assert.equal(payload.recordValidation.evidence.intervalEnd, payload.occurredAt);
assert.equal(Date.parse(payload.recordValidation.evidence.intervalEnd)
  - Date.parse(payload.recordValidation.evidence.intervalStart), payload.lapTimeMs);
const result = evaluateLapRecord({ ...payload, expectedSource: "ASSETTO_SERVER_NATIVE" });
assert.equal(result.accepted, false);
assert.equal(result.reason, "TELEMETRY_INCOMPLETE");
assert.equal(JSON.stringify(payload), original);
console.log("PASS concrete native fixture against Web schema/policy: TELEMETRY_INCOMPLETE, raw UNKNOWN unchanged");
