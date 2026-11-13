import { useState } from "react";
import { DownloadCloud, RefreshCw } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Notice } from "../common/Notice";
import { useNotices } from "../common/NoticesContext";
import { api } from "../../lib/api";

export function CatchupPanel({ company, cooldown, run, running, disabled, onStart, onCompleted }) {
  const { notify } = useNotices();
  const [starting, setStarting] = useState(false);
  const isRunning = running || run?.status === "running";

  async function handleStart() {
    if (starting || isRunning) return;
    if (disabled) {
      notify.error("Configure uma pasta válida no servidor e um certificado A1 para consultar a SEFAZ.");
      return;
    }
    if (cooldown) {
      notify.success(`O botão está disponível. A SEFAZ permite a próxima consulta após ${cooldown.time} (${cooldown.minutes} min). Nenhuma consulta antecipada foi enviada.`);
      return;
    }
    if (!window.confirm(
      "Capturar todos os XMLs de NF-e disponíveis na SEFAZ para este CNPJ?\n\n" +
      "A consulta continuará do NSU salvo até alcançar o máximo informado pela SEFAZ. " +
      "Esta operação AVANÇA o cursor do monitoramento."
    )) return;
    setStarting(true);
    try {
      const result = await api.post("/api/sefaz/nfe/catchup", { companyId: company.companyId });
      onStart?.(result);
    } catch (e) { notify.error(e.message); }
    finally { setStarting(false); }
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <div className="p-2.5 bg-blue-50 rounded-lg text-blue-600"><DownloadCloud size={20} /></div>
          <div>
            <h3 className="text-base font-bold">Consultar todos os XMLs disponíveis</h3>
            <p className="text-sm text-slate-500 mt-0.5">Execute a verificação manual quando quiser. A espera fiscal é informada após o clique.</p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <Notice type="info">
          Sincroniza os lotes disponíveis e avança o mesmo NSU do monitoramento.
          A disponibilidade histórica depende da SEFAZ.
        </Notice>
        <Button className="mt-4" disabled={isRunning || starting} onClick={handleStart}>
          <DownloadCloud size={16} />
          {isRunning ? "Captura na SEFAZ em andamento" : starting ? "Iniciando..." : "Consultar todos os XMLs disponíveis"}
        </Button>
        {run && (
          <div className="mt-4 rounded-lg bg-slate-50 p-3 text-sm">
            <div className="font-semibold mb-2">Estado: {labelFor(run.status)} · Lotes: {run.batches ?? 0}</div>
            <div className="grid grid-cols-2 gap-2 text-xs">
              <span>XMLs novos: <strong>{run.completeSaved ?? 0}</strong></span>
              <span>Já existentes: <strong>{run.alreadyPresent ?? 0}</strong></span>
            </div>
            {run.message && <p className="mt-2 text-xs text-slate-600">{run.message}</p>}
            <details className="mt-2 text-xs text-slate-600">
              <summary className="cursor-pointer">Detalhes técnicos</summary>
              <p className="mt-1">
                Último NSU: {run.lastNsu || "—"} · Resumos/eventos: {run.ignoredNonProcNFe ?? 0}
              </p>
            </details>
            {run.status !== "running" && (
              <Button variant="ghost" size="sm" className="mt-2" onClick={() => onCompleted?.()}>
                <RefreshCw size={14} />Atualizar lista de empresas
              </Button>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function labelFor(status) {
  return ({ running: "Em execução", completed: "Consulta concluída",
    paused: "Pausada", attention: "Requer verificação",
    failed: "Falhou", stopped: "Interrompida" })[status] || status || "—";
}
