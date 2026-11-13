import { useState } from "react";
import { Server, ShieldCheck, Info, RefreshCw, Settings2 } from "lucide-react";
import { Card, CardHeader, CardContent } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Notice } from "../components/common/Notice";
import { HanaForm } from "../components/forms/HanaForm";
import { useNotices } from "../components/common/NoticesContext";
import { api } from "../lib/api";

/** GET /api/health consulta apenas o estado. Provisionar exige credenciais explícitas. */
export function SettingsPage({ hanaReady }) {
  const { notify } = useNotices();
  const [busy, setBusy] = useState(false);
  const [health, setHealth] = useState(null);
  const [showHanaForm, setShowHanaForm] = useState(false);

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

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Configurações</h1>
        <p className="mt-1 text-sm text-slate-500">Administração da conexão fiscal e do ambiente local.</p>
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
