import { Truck, PlugZap, DownloadCloud } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Notice } from "../common/Notice";
import { useNotices } from "../common/NoticesContext";
import { api } from "../../lib/api";
import { Stat } from "../common/Stat";

export function CtePanel({ company, cteState, schedule, cooldown, busy, result, onResult }) {
  const { notify } = useNotices();
  const cteSchedule = schedule?.find((s) => s.documentType === "CTE");

  async function download() {
    if (!cteSchedule?.enabled || !cteSchedule.destinationDirectory?.trim()) {
      notify.error("Ative a captura CT-e, informe a pasta e salve a configuração."); return;
    }
    if (cooldown) {
      notify.success(`CT-e: próxima consulta após ${cooldown.time} (${cooldown.minutes} min).`); return;
    }
    if (!window.confirm("Confirma que não existe outro capturador CT-e ativo? A consulta será enviada à SEFAZ.")) return;
    try {
      const r = await api.post("/api/cte/download", { companyId: company.companyId, previousConsumerStopped: true });
      onResult?.(r);
      if (r.status === 137) notify.success("CT-e: nenhum documento disponível.");
      else notify.success(`CT-e: ${r.filesSaved ?? 0} XML(s) completos gravados.`);
    } catch (e) { notify.error(e.message); }
  }

  async function testConnectivity() {
    try {
      const r = await api.post("/api/capture/test-connectivity", { companyId: company.companyId });
      if (r.reachable) notify.success("Conexão HTTPS com a SEFAZ confirmada.");
      else notify.error(r.message || "Conexão SEFAZ indisponível.");
    } catch (e) { notify.error(e.message); }
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
        <div className="grid gap-3 sm:grid-cols-2">
          <Stat label="CT-e · Último NSU" value={cteState?.lastNsu || "Não inicializado"} mono />
          <Stat label="CT-e · Maior NSU informado" value={cteState?.maxNsu || "Ainda não informado"} mono />
        </div>
        {cooldown && (
          <div className="mt-3"><Notice>Próxima consulta permitida após {cooldown.time} ({cooldown.minutes} min).</Notice></div>
        )}
        <div className="mt-4 flex flex-wrap gap-3">
          <Button disabled={busy || !cteSchedule?.enabled} onClick={download}>
            <DownloadCloud size={16} />{cooldown ? "Aguardando cooldown" : "Consultar CT-e"}
          </Button>
          <Button variant="outline" onClick={testConnectivity}>
            <PlugZap size={16} />Testar conexão SEFAZ
          </Button>
        </div>
        {result && (
          <div className="mt-4">
            <Notice type={result.saveSucceeded === false ? "error"
              : result.status === 137 || (result.filesSaved === 0 && result.filesAlreadyPresent === 0) ? "warning" : "success"}>
              CT-e {result.status}: {result.reason} · procCTe novos: {result.filesSaved ?? 0} ·
              {" "}Outros: {result.skippedNonProcCTe ?? 0}
              {result.archiveUrl && (
                <div className="mt-2"><a href={result.archiveUrl} className="underline">Baixar lote de recuperação CT-e</a></div>
              )}
            </Notice>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
