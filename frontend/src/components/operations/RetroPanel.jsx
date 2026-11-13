import { History } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Notice } from "../common/Notice";
import { useNotices } from "../common/NoticesContext";
import { api } from "../../lib/api";

export function RetroPanel({ company, cooldown, busy, result, disabled, onStart }) {
  const { notify } = useNotices();

  async function handleRetro() {
    if (!window.confirm(
      "Consultar pontualmente uma lacuna de NSU conhecida na SEFAZ?\n\n" +
      "O checkpoint de monitoramento NÃO será alterado. A SEFAZ pode não disponibilizar " +
      "documentos antigos, e não há garantia de recuperar todo o histórico."
    )) return;
    try {
      const r = await api.post(`/api/recovery/nfe/sefaz/${company.companyId}`, {});
      onStart?.(r);
    } catch (e) { notify.error(e.message); }
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <div className="p-2.5 bg-amber-50 rounded-lg text-amber-600"><History size={20} /></div>
          <div>
            <h3 className="text-base font-bold">Recuperação retroativa (consNSU)</h3>
            <p className="text-sm text-slate-500 mt-0.5">Consulta UMA lacuna conhecida entre NSUs já armazenados. Não avança nem recua o cursor do monitoramento automático.</p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <Notice type="warning" title="Não é varredura regressiva universal">
          Não busca em ordem decrescente nem garante documentos anteriores ao primeiro NSU conhecido.
          Esta ação é diferente da sincronização pontual e não substitui o monitoramento automático.
        </Notice>
        <Button className="mt-4" variant="outline" disabled={disabled || busy} onClick={handleRetro}>
          <History size={16} />{busy ? "Consultando lacuna..." : "Recuperar NSU anterior disponível"}
        </Button>
        {result && (
          <p className="mt-3 text-xs text-slate-600">
            {result.message} · NSU: {result.requestedNsu || "não identificado"} ·
            XMLs novos: {result.xmlCreated ?? 0}
          </p>
        )}
      </CardContent>
    </Card>
  );
}
