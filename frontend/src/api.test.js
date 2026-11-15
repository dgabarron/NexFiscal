import test from "node:test";
import assert from "node:assert/strict";
import { api, ApiError, credentials, describeStatus, isValid } from "./api.js";

test("credenciais preservam senha e convertem porta", () => {
  assert.deepEqual(credentials({server:" hana.local ",port:"30015",username:" fiscal ",password:" segredo "}),
    {server:"hana.local",port:30015,username:"fiscal",password:" segredo "});
});

test("substituição de configuração ilegível só é enviada quando confirmada", () => {
  const form = {server:"h",port:"1",username:"u",password:"p"};
  assert.equal("replaceUnreadable" in credentials(form), false);
  assert.equal(credentials(form, true).replaceUnreadable, true);
});

test("validação do formulário", () => {
  const ok = {server:"h",port:"30015",username:"u",password:"p"};
  assert.equal(isValid(ok), true);
  for (const bad of [{server:" "},{port:"0"},{port:"65536"},{port:"abc"},{username:""},{password:""}])
    assert.equal(isValid({...ok, ...bad}), false);
});

test("cada estado do servidor tem apresentação própria", () => {
  const tones = Object.fromEntries(["ready","schema_pending","unavailable","unreadable","not_configured"]
    .map(state => [state, describeStatus({state}).tone]));
  assert.deepEqual(tones, {ready:"ok",schema_pending:"warn",unavailable:"warn",unreadable:"error",not_configured:"idle"});
  assert.equal(describeStatus(null).tone, "idle");
});

test("erro da API carrega mensagem, categoria e itens", async () => {
  globalThis.fetch = async () => ({ ok:false, json: async () => ({error:"Estrutura incompatível",kind:"schema_incompatible",issues:["Coluna ausente: T.C"]}) });
  await assert.rejects(api("/x"), error => error instanceof ApiError && error.message === "Estrutura incompatível" &&
    error.kind === "schema_incompatible" && error.issues.length === 1);
});

test("serviço local parado gera mensagem clara", async () => {
  globalThis.fetch = async () => { throw new TypeError("fetch failed"); };
  await assert.rejects(api("/x"), error => error.kind === "service_offline" && /serviço local/.test(error.message));
});
