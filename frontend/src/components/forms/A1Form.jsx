import { useState } from "react";
import { BadgeCheck, CloudUpload, RefreshCw, CheckCircle2, ArrowRight } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Field } from "../common/Field";
import { Notice } from "../common/Notice";
import { api } from "../../lib/api";
import { useNotices } from "../common/NoticesContext";
import { formatCnpj, issuerDisplayName } from "../../lib/fiscal-format";

function Row({ label, value }) {
  return (
    <div className="flex items-start justify-between gap-5 border-b border-slate-100 py-3 last:border-0">
      <span className="text-sm text-slate-500">{label}</span>
      <span className="text-right text-sm font-medium text-slate-800 break-words max-w-[65%]">{value || "Não identificado"}</span>
    </div>
  );
}

export function A1Form({ onRegistered, showActions = true }) {
  const { notify } = useNotices();
  const [file, setFile] = useState(null);
  const [password, setPassword] = useState("");
  const [identity, setIdentity] = useState(null);
  const [busy, setBusy] = useState(false);

  function reset() { setIdentity(null); notify.error(""); }

  async function inspect(register) {
    const form = new FormData();
    form.append("certificate", file);
    form.append("password", password);
    setBusy(true); notify.error("");
    try {
      const result = await api.post(
        register ? "/api/companies/register" : "/api/certificates/inspect", form
      );
      if (register) {
        setPassword(""); setFile(null); setIdentity(null);
        notify.success("Empresa cadastrada e certificado A1 armazenado de forma protegida.");
        onRegistered?.(result);
      } else {
        setIdentity(result);
      }
    } catch (e) { notify.error(e.message); }
    finally { setBusy(false); }
  }

  return (
    <Card>
      <CardHeader>
        <h2 className="text-lg font-bold">Certificado digital A1</h2>
        <p className="text-sm text-slate-500 mt-0.5">Identificação automática da empresa</p>
      </CardHeader>
      <CardContent>
        <div className="rounded-xl border-2 border-dashed border-slate-200 bg-slate-50 p-6 text-center">
          <CloudUpload className="mx-auto text-blue-500" size={28} />
          <p className="mt-3 font-semibold text-sm">{file ? file.name : "Selecione um arquivo .pfx ou .p12"}</p>
          <p className="text-xs text-slate-400 mt-1">O certificado será criptografado e armazenado no servidor.</p>
          <label htmlFor="cert-file" className="inline-flex mt-4 cursor-pointer items-center rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-100">
            <input id="cert-file" type="file" accept=".pfx,.p12" className="sr-only"
              onChange={(e) => { setFile(e.target.files?.[0] || null); reset(); }} />
            Selecionar certificado
          </label>
        </div>
        <div className="mt-5">
          <Field label="Senha do certificado" htmlFor="cert-pass">
            <Input id="cert-pass" type="password" autoComplete="off"
              value={password} placeholder="Digite a senha do A1"
              onChange={(e) => { setPassword(e.target.value); reset(); }} />
          </Field>
        </div>
        <div className="flex gap-3 mt-5">
          <Button disabled={busy || !file || !password} onClick={() => inspect(false)}>
            {busy ? <RefreshCw size={16} /> : <BadgeCheck size={16} />}Validar certificado
          </Button>
        </div>
        {identity && (
          <div className="mt-6 border-t border-slate-100 pt-5">
            <h3 className="flex items-center gap-2 font-bold text-slate-800">
              <CheckCircle2 size={18} className="text-emerald-500" />Dados identificados
            </h3>
            <div className="mt-3">
              <Row label="Razão social" value={identity.legalName} />
              <Row label="CNPJ" value={formatCnpj(identity.cnpj)} />
              <Row label="UF" value={identity.state || "Não informada no Subject"} />
              <Row label="Validade" value={new Date(identity.validUntil).toLocaleDateString("pt-BR")} />
              <Row label="Autoridade certificadora" value={issuerDisplayName(identity.issuer)} />
            </div>
            <div className="mt-4">
              <Notice>
                Validação de cadeia ICP-Brasil, revogação e autorização SEFAZ ainda pendentes.
                O cadastro permanecerá pausado até a inicialização fiscal segura.
              </Notice>
            </div>
            {showActions && (
              <Button className="mt-5" disabled={busy} onClick={() => inspect(true)}>
                Cadastrar empresa<ArrowRight size={16} />
              </Button>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  );
}
