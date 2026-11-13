import { History } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Notice } from "../common/Notice";

export function RetroPanel({ company, cooldown, busy, result, disabled, onExecute, catchupRunning }) {
  async function handleRetro() {
    if (disabled || busy) return;
    if (!window.confirm(
      "Consultar pontualmente uma lacuna de NSU conhecida na SEFAZ?\n\n" +
      "O checkpoint do monitoramento NÃO será alterado. " +
      "A SEFAZ pode não disponibilizar documentos anteriores."
    )) return;
    await onExecute?.(company.companyId);
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <div className="rounded-lg bg-amber-50 p-2.5 text-amber-600"><History size={20} /></div>
          <div>
            <h3 className="text-base font-bold">Recuperação retroativa (consNSU)</h3>
            <p className="mt-1 text-sm text-slate-500">
              Consulta uma lacuna conhecida entre NSUs armazenados, sem avançar ou recuar o cursor normal.
            </p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <Notice type="info">
          Consulta uma lacuna conhecida de NSU. A SEFAZ pode não disponibilizar documentos antigos.
        </Notice>
        {cooldown && <p className="mt-3 text-sm text-amber-800">
          Próxima consulta permitida após {cooldown.time} ({cooldown.minutes} min).
        </p>}
        {catchupRunning && <p className="mt-3 text-sm text-amber-800">
          Sincronização sequencial em andamento: aguarde antes de retroagir.
        </p>}
        <Button className="mt-4" variant="outline" disabled={disabled || busy} onClick={handleRetro}>
          <History size={16} />{busy ? "Consultando lacuna..." : "Recuperar NSU anterior disponível"}
        </Button>
        {result && (
          <div className="mt-3 text-sm">
            <Notice type={result.state === "completed" ? "success" :
              result.state === "rejected" || result.state === "paused" ? "warning" : "info"}>
              {result.message} · NSU: {result.requestedNsu || "não identificado"} ·
              XMLs novos: {result.xmlCreated ?? 0} · Já existentes: {result.alreadyPresent ?? 0}
            </Notice>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
