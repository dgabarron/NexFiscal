import test from "node:test";
import assert from "node:assert/strict";
import {
  pendingWindow, parseUtcTimestamp, defaultSchedule, mergeSchedule,
  isValidInterval,
} from "./capture-utils.js";

test("UTC sem Z é interpretado como UTC e janela fiscal é respeitada", () => {
  const now = Date.parse("2026-11-13T10:00:00Z");
  const pending = pendingWindow({ nextAllowedUtc: "2026-11-13T11:00:00" }, now);
  assert.equal(pending.iso, "2026-11-13T11:00:00.000Z");
  assert.equal(pending.minutes, 60);
  assert.equal(pendingWindow({ nextAllowedUtc: "2026-11-13T09:59:59Z" }, now), null);
  assert.equal(pendingWindow({}, now), null);
  assert.equal(parseUtcTimestamp("2026-11-13T11:00:00")?.toISOString(), pending.iso);
});

test("configurações preservam os três tipos e não ativam NFS-e por padrão", () => {
  assert.deepEqual(defaultSchedule().map(s => [s.documentType, s.enabled]), [
    ["NFE", false], ["CTE", false], ["NFSE", false],
  ]);
  const saved = mergeSchedule([{ documentType: "CTE", enabled: true,
    intervalMinutes: 45, destinationDirectory: "C:\\NexFiscal\\CTE" }]);
  assert.equal(saved.find(s => s.documentType === "CTE").intervalMinutes, "45");
  assert.equal(saved.find(s => s.documentType === "CTE").enabled, true);
  assert.equal(saved.find(s => s.documentType === "NFSE").enabled, false);
});

test("intervalo usa Int64 positivo e rejeita formato indevido", () => {
  for (const valid of ["1", "60", "9223372036854775807"]) assert.equal(isValidInterval(valid), true);
  for (const invalid of ["0", "-1", "1.5", "texto", "", "9223372036854775808"])
    assert.equal(isValidInterval(invalid), false);
});
