import test from "node:test";
import assert from "node:assert/strict";
import { api, ApiError } from "./api.js";

test("POST preserva endpoint, corpo JSON e content-type", async () => {
  const old = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (path, init) => {
    calls.push({ path, init });
    return { ok: true, json: async () => ({ status: 138 }) };
  };
  try {
    const result = await api.post("/api/capture/download", { companyId: "sample-id" });
    assert.equal(result.status, 138);
    assert.equal(calls[0].path, "/api/capture/download");
    assert.equal(calls[0].init.headers["Content-Type"], "application/json");
    assert.deepEqual(JSON.parse(calls[0].init.body), { companyId: "sample-id" });
  } finally { globalThis.fetch = old; }
});

test("GET de status não dispara POST ou chamada fiscal", async () => {
  const old = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (path, init) => {
    calls.push({ path, method: init.method });
    return { ok: true, json: async () => ({ status: "running" }) };
  };
  try {
    await api.get("/api/sefaz/nfe/catchup/sample-id");
    assert.deepEqual(calls, [{ path: "/api/sefaz/nfe/catchup/sample-id", method: "GET" }]);
  } finally { globalThis.fetch = old; }
});

test("HTTP 409 preserva erro legível em vez de informar sucesso", async () => {
  const old = globalThis.fetch;
  globalThis.fetch = async () => ({
    ok: false, status: 409, json: async () => ({ error: "Janela SEFAZ indisponível." }),
  });
  try {
    await assert.rejects(api.post("/api/recovery/nfe/sefaz/sample-id", {}),
      e => e instanceof ApiError && e.status === 409 && /Janela/.test(e.message));
  } finally { globalThis.fetch = old; }
});
