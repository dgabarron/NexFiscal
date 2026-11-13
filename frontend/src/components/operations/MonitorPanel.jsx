import { useState } from "react";
import { Activity, Save } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { useNotices } from "../common/NoticesContext";
import { api } from "../../lib/api";
import { isValidInterval } from "../../lib/capture-utils";

export function MonitorPanel({ company, schedule, onScheduleField, onSaved, documentType = "NFE" }) {
  const { notify } = useNotices();
  const [saving, setSaving] = useState(false);
  const nfe = schedule.find((s) => s.documentType === documentType);
  if (!nfe) return null;

  async function save() {
    if (saving) return;
    if (!schedule.every(s => isValidInterval(s.intervalMinutes))) {
      notify.error("Informe intervalos válidos em todos os tipos de documento."); return;
    }
    if (nfe.enabled && !nfe.destinationDirectory.trim()) { notify.error("Preencha a pasta de destino para ativar a captura."); return; }
    setSaving(true);
    try {
      const entries = schedule.map((s) => ({ ...s, intervalMinutes: String(s.intervalMinutes).trim(),
        automaticConfirmed: s.documentType === "CTE" && documentType === "CTE"
          ? !!s.enabled : !!s.automaticConfirmed }));
      const result = await api.post(`/api/capture/schedules/${company.companyId}`, entries);
      await onSaved?.(result);
      notify.success(nfe.enabled
        ? "Monitoramento automático configurado."
        : "Monitoramento automático desativado. A consulta manual continua disponível.");
    } catch (e) { notify.error(e.message); }
    finally { setSaving(false); }
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <div className="p-2.5 bg-emerald-50 rounded-lg text-emerald-600"><Activity size={20} /></div>
          <div>
            <h3 className="text-base font-bold">Monitoramento automático de {documentType === "CTE" ? "CT-e" : "NF-e"}</h3>
            <p className="mt-1 text-sm text-slate-500">Configure a captura recorrente e a pasta usada também na consulta manual.</p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-slate-50 p-3">
          <label className="flex items-center gap-2 text-sm font-semibold">
            <input type="checkbox" checked={nfe.enabled}
              onChange={(e) => onScheduleField(company.companyId, documentType, "enabled", e.target.checked)} />
            Ativar monitoramento
          </label>
          <div className="flex items-center gap-2">
            <label htmlFor="nfe-interval" className="text-xs text-slate-600">Intervalo (min)</label>
            <input id="nfe-interval" type="text" inputMode="numeric" pattern="[0-9]*"
              className="w-24 rounded-md border border-slate-300 bg-white p-2 text-sm"
              value={nfe.intervalMinutes}
              onChange={(e) => onScheduleField(company.companyId, documentType, "intervalMinutes", e.target.value)} />
          </div>
        </div>
        <div className="mt-3">
          <label htmlFor="nfe-dir" className="mb-1 block text-xs text-slate-600">Pasta dos XMLs (monitoramento e consulta manual)</label>
          <input id="nfe-dir" type="text"
            className="w-full rounded-md border border-slate-300 p-2 text-sm disabled:bg-slate-100 disabled:text-slate-400"
            placeholder="Ex.: C:\\NexFiscal\\XML"
            value={nfe.destinationDirectory}
            onChange={(e) => onScheduleField(company.companyId, documentType, "destinationDirectory", e.target.value)} />
        </div>
        <Button className="mt-4" variant="outline" disabled={saving} onClick={save}>
          <Save size={16} />{saving ? "Salvando..." : "Salvar configurações"}
        </Button>
      </CardContent>
    </Card>
  );
}
