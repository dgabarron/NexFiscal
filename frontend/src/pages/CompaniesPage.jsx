import { useState } from "react";
import { Building2, Plus, CheckCircle2, AlertCircle, Trash2, RefreshCw, ShieldCheck } from "lucide-react";
import { Card, CardContent } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { A1Form } from "../components/forms/A1Form";
import { Notice } from "../components/common/Notice";
import { api } from "../lib/api";
import { formatCnpj } from "../lib/fiscal-format";

function CompanyCard({ company, onChanged }) {
  const [mode, setMode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [certificate, setCertificate] = useState(null);
  const [password, setPassword] = useState("");
  const hasCertificate = company.certificateStored === true;
  const unreadableCertificate = company.certificateStatus === "unreadable";
  const expected = String(company.cnpj).replace(/\D/g, "");

  async function remove() {
    if (busy || confirmation.replace(/\D/g, "") !== expected) return;
    setBusy(true); setError("");
    try {
      await api.post(`/api/companies/${company.companyId}/remove`, { cnpjConfirmation: expected });
      await onChanged?.();
    } catch (e) { setError(e.message); }
    finally { setBusy(false); }
  }

  async function recover(event) {
    event.preventDefault();
    if (busy || !certificate || !password) return;
    const form = new FormData();
    form.append("certificate", certificate);
    form.append("password", password);
    setBusy(true); setError("");
    try {
      await api.post(`/api/companies/${company.companyId}/certificate`, form);
      setPassword(""); setCertificate(null); setMode("");
      await onChanged?.();
    } catch (e) { setError(e.message); }
    finally { setBusy(false); }
  }

  return (
    <Card>
      <CardContent className="pt-6">
        <div className="flex items-start gap-3">
          <div className="rounded-lg bg-blue-50 p-2 text-blue-600"><Building2 size={20} /></div>
          <div className="min-w-0 flex-1">
            <h3 className="font-bold text-slate-900">{company.legalName}</h3>
            <p className="mt-1 text-sm text-slate-500">CNPJ {formatCnpj(company.cnpj)}</p>
            <div className="mt-3 flex items-center gap-1.5 text-xs">
              {hasCertificate ? (
                <><CheckCircle2 size={15} className="text-emerald-600" /><span className="text-emerald-800">A1 armazenado no servidor</span></>
              ) : (
                <><AlertCircle size={15} className="text-amber-600" /><span className="text-amber-800">{unreadableCertificate ? "A1 armazenado, mas inacessível" : "Arquivo A1 não localizado no servidor"}</span></>
              )}
            </div>
            <p className="mt-2 text-xs text-slate-500">
              {hasCertificate
                ? "Arquivo protegido acessível. A validade e a autorização SEFAZ são verificadas separadamente."
                : unreadableCertificate
                  ? "O arquivo existe, mas não pode ser aberto com as chaves desta instalação. Preserve o cofre e as chaves originais; não recadastre a empresa."
                  : "A empresa está cadastrada no HANA, mas o A1 não foi localizado. Reenvie o certificado para este cadastro sem alterar o CNPJ."}
            </p>
          </div>
        </div>
        <div className="mt-4 flex flex-wrap gap-2">
          {!hasCertificate && !unreadableCertificate && (
            <Button variant="outline" size="sm" onClick={() => { setMode(mode === "certificate" ? "" : "certificate"); setError(""); }}>
              <ShieldCheck size={15} />{mode === "certificate" ? "Cancelar" : "Reenviar A1"}
            </Button>
          )}
          <Button variant="outline" size="sm" onClick={() => { setMode(mode === "remove" ? "" : "remove"); setConfirmation(""); setError(""); }}>
            <Trash2 size={15} />{mode === "remove" ? "Cancelar" : "Remover empresa"}
          </Button>
        </div>
        {mode === "certificate" && (
          <form onSubmit={recover} className="mt-4 space-y-3 border-t border-slate-100 pt-4">
            <p className="text-sm text-slate-700">Selecione o A1 deste CNPJ para recuperar o certificado sem duplicar a empresa.</p>
            <input type="file" accept=".pfx,.p12" required
              onChange={e => setCertificate(e.target.files?.[0] || null)}
              className="block w-full text-xs" />
            <input type="password" autoComplete="off" required value={password}
              onChange={e => setPassword(e.target.value)}
              placeholder="Senha do certificado"
              className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
            <Button type="submit" size="sm" disabled={busy || !certificate || !password}>
              <RefreshCw size={15}/>{busy ? "Salvando..." : "Salvar A1 neste cadastro"}
            </Button>
          </form>
        )}
        {mode === "remove" && (
          <div className="mt-4 space-y-3 border-t border-slate-100 pt-4">
            <Notice type="warning">
              A exclusão só é permitida se a empresa não possui XMLs, logs ou checkpoints fiscais.
              Isso impede a perda de documentos já capturados.
            </Notice>
            <label htmlFor={`confirm-${company.companyId}`} className="block text-xs font-semibold text-slate-700">
              Confirme o CNPJ para remover o cadastro
            </label>
            <input id={`confirm-${company.companyId}`} value={confirmation}
              onChange={e => setConfirmation(e.target.value)}
              placeholder={formatCnpj(company.cnpj)}
              className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
            <Button type="button" size="sm" disabled={busy || confirmation.replace(/\D/g, "") !== expected}
              onClick={remove}>
              <Trash2 size={15} />{busy ? "Removendo..." : "Confirmar remoção"}
            </Button>
          </div>
        )}
        {error && <div className="mt-3"><Notice type="error">{error}</Notice></div>}
      </CardContent>
    </Card>
  );
}

export function CompaniesPage({ companies, onCompanyAdded, onCompanyChanged }) {
  const [adding, setAdding] = useState(false);
  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-bold text-slate-900">Empresas e certificados</h1>
          <p className="mt-1 text-sm text-slate-500">
            Gerencie os cadastros e certificados A1 das empresas.
          </p>
        </div>
        <Button onClick={() => setAdding(v => !v)}>
          <Plus size={16} />{adding ? "Cancelar" : "Adicionar empresa"}
        </Button>
      </div>
      {adding && <A1Form onRegistered={async result => {
        setAdding(false);
        await onCompanyAdded?.(result);
      }} />}
      {companies.length === 0 && !adding &&
        <p className="text-sm text-slate-500">Nenhuma empresa cadastrada.</p>}
      <div className="grid gap-4 sm:grid-cols-2">
        {companies.map(company => (
          <CompanyCard key={company.companyId} company={company} onChanged={onCompanyChanged} />
        ))}
      </div>
    </div>
  );
}
