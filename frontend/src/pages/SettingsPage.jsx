import { useState } from "react";
import { Server, ShieldCheck, Info, RefreshCw, Settings2, PlugZap } from "lucide-react";
import { Card, CardHeader, CardContent } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Notice } from "../components/common/Notice";
import { HanaForm } from "../components/forms/HanaForm";
import { useNotices } from "../components/common/NoticesContext";
import { api } from "../lib/api";

/** GET /api/health consulta apenas o estado. Provisionar exige credenciais explícitas. */
export function SettingsPage({ hanaReady, companies = [] }) {
  const { notify } = useNotices();
  const [busy, setBusy] = useState(false);
  const [health, setHealth] = useState(null);
  const [showHanaForm, setShowHanaForm] = useState(false);
  const [diagnosticCompanyId, setDiagnosticCompanyId] = useState("");
  const [diagnosticBusy, setDiagnosticBusy] = useState(false);

  async function refresh() {
    if (busy) return;
    setBusy(true);
    try {
      const result = await api.get("/api/health");
      setHealth(result);
      notify.success("Estado da instalação atualizado.");
    } catch (e) { notify.error(e.message); }
    finally { setBusy(false); }
  }

  async function testSefaz() {
    const id = companies.some(c => c.companyId === diagnosticCompanyId)
      ? diagnosticCompanyId : companies[0]?.companyId;
    if (!id || diagnosticBusy) return;
    setDiagnosticBusy(true);
    try {
      const response = await api.post("/api/capture/test-connectivity", { companyId: id });
      if (response.reachable)
        notify.success("Conexão HTTPS com a SEFAZ confirmada. Isso não verifica autorização fiscal do CNPJ.");
      else notify.error(response.message || "Não foi possível alcançar a SEFAZ.");
    } catch (error) { notify.error(error.message); }
    finally { setDiagnosticBusy(false); }
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Configuração técnica</h1>
        <p className="mt-1 text-sm text-slate-500">Conexão HANA, diagnóstico e manutenção da instalação.</p>
      </div>
      <Card>
        <CardHeader>
          <div className="flex items-center gap-3">
            <Server size={20} className="text-blue-600" />
            <div><h3 className="text-base font-bold">SAP HANA</h3>
              <p className="mt-1 text-sm text-slate-500">Schema BRAI_NEX</p>
            </div>
          </div>
        </CardHeader>
        <CardContent>
          <div className="flex items-center gap-2 text-sm">
            {hanaReady
              ? <><ShieldCheck size={18} className="text-emerald-600" />Conexão salva no servidor</>
              : <><Info size={18} className="text-amber-600" />Conexão não configurada</>}
          </div>
          {health && (
            <p className="mt-3 text-sm text-slate-600">
              Configuração persistida: {health.hanaConfigured ? "sim" : "não"} ·
              Instalação inicial concluída: {health.hanaSetupCompleted ? "sim" : "não"}
            </p>
          )}
          <div className="mt-4 flex flex-wrap gap-3">
            <Button variant="outline" disabled={busy} onClick={refresh}>
              <RefreshCw size={16} />{busy ? "Atualizando..." : "Atualizar estado"}
            </Button>
            <Button variant="outline" onClick={() => setShowHanaForm(v => !v)}>
              <Settings2 size={16} />{showHanaForm ? "Fechar formulário" : "Reconfigurar conexão"}
            </Button>
          </div>
          <div className="mt-4">
            <Notice type="info">
              A consulta de estado não executa comandos no HANA nem verifica as cinco tabelas.
              Para testar a conexão ou preparar a estrutura, informe explicitamente os dados de acesso
              no formulário de administração. Nenhuma credencial é recuperada para o navegador.
            </Notice>
          </div>
        </CardContent>
      </Card>
      {showHanaForm && (
        <div className="space-y-3">
          <Notice type="warning">
            Área técnica: salvar outra conexão altera a configuração utilizada pelo NexFiscal.
            Verifique o destino e a base antes de confirmar.
          </Notice>
          <HanaForm onSaved={() => { setShowHanaForm(false); refresh(); }} />
        </div>
      )}
      <Card>
        <CardHeader>
          <h3 className="text-base font-bold">Diagnóstico SEFAZ</h3>
          <p className="mt-1 text-sm text-slate-500">
            Verifica conectividade HTTPS e certificado, sem executar distribuição fiscal.
          </p>
        </CardHeader>
        <CardContent>
          {companies.length ? (
            <div className="flex flex-wrap items-end gap-3">
              <div className="flex min-w-0 flex-1 flex-col gap-1">
                <label htmlFor="diag-company" className="text-xs font-semibold text-slate-700">Empresa</label>
                <select id="diag-company"
                  value={companies.some(c => c.companyId === diagnosticCompanyId)
                    ? diagnosticCompanyId : companies[0].companyId}
                  onChange={(event) => setDiagnosticCompanyId(event.target.value)}
                  className="w-full rounded-lg border border-slate-300 bg-white p-2 text-sm">
                  {companies.map(company => (
                    <option key={company.companyId} value={company.companyId}>{company.legalName}</option>
                  ))}
                </select>
              </div>
              <Button variant="outline" onClick={testSefaz} disabled={diagnosticBusy}>
                <PlugZap size={16} />{diagnosticBusy ? "Verificando..." : "Testar conexão"}
              </Button>
            </div>
          ) : <p className="text-sm text-slate-600">Cadastre uma empresa para testar a conexão.</p>}
        </CardContent>
      </Card>
      <Card>
        <CardHeader><h3 className="text-base font-bold">Sobre esta instalação</h3></CardHeader>
        <CardContent>
          <ul className="space-y-1.5 list-disc pl-5 text-sm text-slate-600">
            <li>Frontend em validação na branch de QA; não é versão de produção.</li>
            <li>Portal disponível somente em loopback no backend atual.</li>
            <li>Autenticação e exposição segura na rede dependem de implementação no servidor.</li>
            <li>Integração operacional com SAP Business One não está incluída nesta entrega.</li>
          </ul>
        </CardContent>
      </Card>
    </div>
  );
}
