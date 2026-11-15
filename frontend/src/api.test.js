import test from "node:test";
import assert from "node:assert/strict";
import { api, ApiError, credentials, describeCertificate, describeStatus, formatDate, isValid, removalWarning } from "./api.js";

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

test("selo do certificado reflete o estado real informado pelo servidor", () => {
  const label = c => describeCertificate(c).tone + ":" + describeCertificate(c).label;
  assert.equal(label({certificateStatus:"stored",daysRemaining:200}), "ok:A1 guardado e válido");
  assert.equal(label({certificateStatus:"stored",daysRemaining:12}), "warn:A1 vence em 12 dia(s)");
  assert.equal(label({certificateStatus:"expired"}), "error:A1 vencido");
  assert.equal(label({certificateStatus:"missing"}), "error:A1 não encontrado");
  assert.equal(label({certificateStatus:"unreadable"}), "error:A1 ilegível");
  assert.equal(label({certificateStatus:"invalid"}), "error:A1 inválido");
  assert.equal(label({certificateStatus:"stored",archived:true}), "warn:Empresa arquivada");
});

test("aviso de remoção distingue exclusão de arquivamento", () => {
  assert.match(removalWarning({legalName:"X",hasFiscalHistory:false}), /excluídos definitivamente/);
  assert.match(removalWarning({legalName:"X",hasFiscalHistory:true}), /apenas arquivada/);
});

test("datas inválidas não quebram a tela", () => {
  assert.equal(formatDate(null), "—");
  assert.equal(formatDate("lixo"), "—");
  assert.match(formatDate("2027-10-16T12:00:00Z"), /2027/);
});
