import test from "node:test";
import assert from "node:assert/strict";
import { credentials } from "./api.js";

test("credenciais preservam senha e convertem porta", () => {
  assert.deepEqual(credentials({server:" hana.local ",port:"30015",username:" fiscal ",password:"segredo"}),
    {server:"hana.local",port:30015,username:"fiscal",password:"segredo"});
});
