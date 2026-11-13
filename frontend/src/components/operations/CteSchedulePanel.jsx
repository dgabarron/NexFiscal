import { useState } from "react";
import { Save, Folder } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { useNotices } from "../common/NoticesContext";
import { api } from "../../lib/api";
import { isValidInterval } from "../../lib/capture-utils";

/** Configuração CT-e autoriza consultas manuais; não cria Worker automático. */
export function CteSchedulePanel({ company, schedules, onField, onSaved }) {
  const { notify } = useNotices();
  const [saving, setSaving] = useState(false);
  const cte = schedules?.find((s) => s.documentType === "CTE");
  if (!cte) return null;

  async function save() {
    if (saving) return;
    if (!schedules.every((s) => isValidInterval(s.intervalMinutes))) {
      notify.error("Informe intervalos válidos para NF-e, CT-e e NFS-e."); return;
    }
    if (cte.enabled && !cte.destinationDirectory?.trim()) {
      notify.error("Informe a pasta de XMLs CT-e no servidor antes de ativar a captura."); return;
    }
    setSaving(true);
    try {
      const payload = schedules.map((s) => ({
        ...s, intervalMinutes: String(s.intervalMinutes).trim(),
      }));
      const result = await api.post(`/api/capture/schedules/${company.companyId}`, payload);
      await onSaved?.(company.companyId, result);
      notify.success(cte.enabled ? "Captura manual CT-e habilitada." : "Captura CT-e desabilitada.");
    } catch (e) { notify.error(e.message); }
    finally { setSaving(false); }
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <Folder size={20} className="text-blue-600" />
          <div>
            <h3 className="text-base font-bold">Configurar CT-e</h3>
            <p className="mt-1 text-sm text-slate-500">
              Defina a pasta onde os XMLs completos de CT-e serão salvos.
            </p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <label className="flex items-center gap-2 text-sm font-semibold">
          <input type="checkbox" checked={cte.enabled}
            onChange={(e) => onField(company.companyId, "CTE", "enabled", e.target.checked)} />
          Permitir captura manual de CT-e
        </label>
        <div className="mt-4">
          <label htmlFor="cte-directory" className="mb-1 block text-xs text-slate-600">
            Pasta de XMLs no servidor
          </label>
          <input id="cte-directory" type="text" value={cte.destinationDirectory || ""}
            placeholder="Ex.: C:\\NexFiscal\\CTE"
            className="w-full rounded-md border border-slate-300 p-2 text-sm"
            onChange={(event) => onField(company.companyId, "CTE", "destinationDirectory", event.target.value)} />
        </div>
        <p className="mt-2 text-xs text-slate-500">
          A pasta precisa existir no servidor. A janela de consultas é controlada pelo backend.
        </p>
        <Button className="mt-4" variant="outline" disabled={saving} onClick={save}>
          <Save size={16} />{saving ? "Salvando..." : "Salvar configuração CT-e"}
        </Button>
      </CardContent>
    </Card>
  );
}
