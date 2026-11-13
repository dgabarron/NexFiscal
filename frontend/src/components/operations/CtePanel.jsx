import { useState } from "react";
import { Truck, DownloadCloud } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Notice } from "../common/Notice";
import { useNotices } from "../common/NoticesContext";
import { api } from "../../lib/api";
import { Stat } from "../common/Stat";

export function CtePanel({ company, cteState, schedule, cooldown, result, onResult }) {
  const { notify } = useNotices();
  const [busy, setBusy] = useState(false);
  const cteSchedule = schedule?.find((s) => s.documentType === "CTE");

  async function download() {
    if (busy) return;
    if (!cteSchedule?.enabled || !cteSchedule.destinationDirectory?.trim()) {
      notify.error("Ative a captura CT-e, informe a pasta e salve a configuração."); return;
    }
    if (cooldown) {
      notify.success(`A consulta CT-e estará permitida após ${cooldown.time} (${cooldown.minutes} min). Nenhuma chamada antecipada foi feita.`);
      return;
    }
    if (!window.confirm("Confirma que não existe outro capturador CT-e ativo? A consulta será enviada à SEFAZ.")) return;
    setBusy(true);
    try {
      const r = await api.post("/api/cte/download", { companyId: company.companyId, previousConsumerStopped: true });
      onResult?.(r);
      if (r.saveSucceeded === false) notify.error(r.message || "Falha ao salvar os XMLs CT-e.");
      else if (r.status === 656) notify.error("SEFAZ 656: consulta bloqueada. Aguarde o intervalo fiscal.");
      else if (r.status === 137) notify.success("CT-e: nenhum documento disponível.");
      else notify.success(`CT-e: ${r.filesSaved ?? 0} XML(s) completos gravados.`);
    } catch (e) { notify.error(e.message); }
    finally { setBusy(false); }
  }


  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <div className="p-2.5 bg-blue-50 rounded-lg text-blue-600"><Truck size={20} /></div>
          <div>
            <h3 className="text-base font-bold">Consulta CT-e (manual)</h3>
            <p className="text-sm text-slate-500 mt-0.5">Uma consulta por vez. Cursor CT-e é independente do cursor NF-e.</p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <details className="rounded-lg border border-slate-200 p-3">
          <summary className="cursor-pointer text-xs font-semibold text-slate-600">Detalhes técnicos de NSU</summary>
          <div className="mt-3 grid gap-3 sm:grid-cols-2">
            <Stat label="Último NSU CT-e" value={cteState?.lastNsu || "Não inicializado"} mono />
            <Stat label="Maior NSU informado CT-e" value={cteState?.maxNsu || "Ainda não informado"} mono />
          </div>
        </details>
        {cooldown && (
          <div className="mt-3"><Notice>Próxima consulta permitida após {cooldown.time} ({cooldown.minutes} min).</Notice></div>
        )}
        <div className="mt-4 flex flex-wrap gap-3">
          <Button disabled={busy || !company.certificateStored || !cteSchedule?.enabled || !cteSchedule?.destinationDirectory?.trim()} onClick={download}>
            <DownloadCloud size={16} />{busy ? "Consultando..." : "Consultar CT-e"}
          </Button>

        </div>
        {result && (
          <div className="mt-4">
            <Notice type={result.saveSucceeded === false ? "error"
              : result.status === 137 || (result.filesSaved === 0 && result.filesAlreadyPresent === 0) ? "warning" : "success"}>
              CT-e {result.status}: {result.reason} · procCTe novos: {result.filesSaved ?? 0} ·
              {" "}Outros: {result.skippedNonProcCTe ?? 0}
            </Notice>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
