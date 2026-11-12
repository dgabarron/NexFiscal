import test from "node:test";
import assert from "node:assert/strict";
import { formatCnpj, formatCpf, issuerDisplayName } from "./fiscal-format.js";

test("CNPJ é mascarado para exibição", () => {
  assert.equal(formatCnpj("14492564000101"), "14.492.564/0001-01");
  assert.equal(formatCnpj("14.492.564/0001-01"), "14.492.564/0001-01");
});
test("CPF é mascarado para exibição", () => {
  assert.equal(formatCpf("12345678909"), "123.456.789-09");
  assert.equal(formatCpf("123.456.789-09"), "123.456.789-09");
});
test("não inventa identificação quando o valor não tem o comprimento esperado", () => {
  assert.equal(formatCpf(null), "Não informado");
  assert.equal(formatCnpj("123"), "123");
});
test("mostra somente o CN do emissor X.509", () => {
  const dn = "CN=AC Certisign RFB G5, OU=Secretaria da Receita Federal do Brasil - RFB, O=ICP-Brasil, C=BR";
  assert.equal(issuerDisplayName(dn), "AC Certisign RFB G5");
  assert.equal(issuerDisplayName("O=ICP-Brasil, CN=Autoridade\\, Brasil, C=BR"), "Autoridade, Brasil");
});
