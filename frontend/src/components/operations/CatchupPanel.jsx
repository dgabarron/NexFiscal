import { useState } from "react";
import { DownloadCloud, RefreshCw } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Notice } from "../common/Notice";
import { useNotices } from "../common/NoticesContext";
import { api } from "../../lib/api";

export function CatchupPanel({ company, cooldown, run, running, disabled, onStart, onCompleted, documentType = "NFE" }) {
  const { notify } = useNotices();
  const [starting, setStarting] = useState(false);
  const isCte = documentType === "CTE";
  const isRunning = running || run?.status === "running" || run?.status === "scheduled";

  async function handleStart() {
    if (starting || isRunning) return;
    if (disabled) {
      notify.error("Configure uma pasta válida no servidor e um certificado A1 para consultar a SEFAZ.");
      return;
    }
    setStarting(true);
    try {
      const result = await api.post(isCte ? "/api/sefaz/cte/catchup" : "/api/sefaz/nfe/catchup", { companyId: company.companyId });
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
            <h3 className="text-base font-bold">Capturar {isCte ? "CT-e" : "NF-e"} agora</h3>
            <p className="mt-1 text-sm text-slate-500">Solicite a captura dos documentos disponibilizados para esta empresa.</p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        {cooldown && (
          <Notice type="info">
            A SEFAZ exige aguardar até {cooldown.time}. Você pode solicitar a captura agora:
            ela ficará agendada no servidor e começará quando for permitida.
          </Notice>
        )}
        <Button className="mt-4" disabled={isRunning || starting} onClick={handleStart}>
          <DownloadCloud size={16} />
          {run?.status === "scheduled" ? "Captura agendada" : isRunning ? "Capturando documentos..." : starting ? "Registrando solicitação..." : "Capturar XMLs disponíveis"}
        </Button>
        {run && (
          <div aria-live="polite" className="mt-4 rounded-lg bg-slate-50 p-4 text-sm">
            <div className="font-semibold mb-2">Estado: {labelFor(run.status)} · Lotes: {run.batches ?? 0}</div>
            <div className="grid grid-cols-2 gap-2 text-xs">
              <span>XMLs novos: <strong>{run.completeSaved ?? 0}</strong></span>
              <span>Já existentes: <strong>{run.alreadyPresent ?? 0}</strong></span>
            </div>
            {run.message && <p className="mt-2 text-xs text-slate-600">{run.message}</p>}
            <p className="mt-2 text-xs text-slate-500">
              Resumos e eventos que não são XMLs completos: {isCte ? run.ignoredNonProcCTe ?? 0 : run.ignoredNonProcNFe ?? 0}.
            </p>
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
    scheduled: "Agendada", paused: "Pausada", attention: "Requer verificação",
    failed: "Falhou", stopped: "Interrompida" })[status] || status || "—";
}
