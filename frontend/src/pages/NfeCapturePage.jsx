import { useMemo, useState } from "react";
import { Building2 } from "lucide-react";
import { CompanyPicker } from "../components/operations/CompanyPicker";
import { MonitorPanel } from "../components/operations/MonitorPanel";
import { CatchupPanel } from "../components/operations/CatchupPanel";
import { ManualNfePanel } from "../components/operations/ManualNfePanel";
import { Notice } from "../components/common/Notice";
import { useCatchupPolling } from "../hooks/useCatchupPolling";
import { pendingWindow } from "../lib/capture-utils";
import { EmptyState } from "../components/common/EmptyState";

export function NfeCapturePage({
  companies, cteStates, schedules, clockNow,
  selectedCompanyId, onSelectCompany,
  scheduleField, onScheduleSaved, onRefreshCapture,
}) {
  const company = useMemo(
    () => companies.find((c) => c.companyId === selectedCompanyId) || companies[0],
    [companies, selectedCompanyId]
  );
  const schedule = company ? schedules[company.companyId] || [] : [];
  const cooldown = company ? pendingWindow(company, clockNow) : null;
  const [runs, setRuns] = useState({});
  useCatchupPolling(runs, setRuns, companies);

  if (!company) {
    return <EmptyState icon={Building2} title="Nenhuma empresa" description="Cadastre uma empresa primeiro." />;
  }
  const run = runs[company.companyId];
  const running = run?.status === "running";
  const nfeSchedule = schedule.find((s) => s.documentType === "NFE");
  const captureDisabled = !company.certificateStored || !!cooldown ||
    !nfeSchedule?.enabled || !nfeSchedule.destinationDirectory?.trim();

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Captura NF-e</h1>
        <p className="text-sm text-slate-500 mt-1">
          Monitoramento automático e sincronização pontual. São operações distintas: o
          monitoramento é contínuo; a sincronização é uma consulta manual que avança o mesmo
          cursor.
        </p>
      </div>
      <CompanyPicker companies={companies} selectedId={company.companyId} onChange={onSelectCompany} />
      {schedule.length === 0 ? <Notice type="error">Não foi possível carregar as configurações de captura.</Notice> :
        <MonitorPanel company={company} schedule={schedule} onScheduleField={scheduleField}
          onSaved={(result) => onScheduleSaved?.(company.companyId, result)} />}
      {captureDisabled && !cooldown && <Notice type="info">Para consultar a SEFAZ, configure e ative a captura NF-e e confirme que existe certificado A1.</Notice>}
      <CatchupPanel
        company={company}
        cooldown={cooldown}
        run={run}
        running={running}
        disabled={captureDisabled}
        onStart={(result) => setRuns((prev) => ({ ...prev, [company.companyId]: result }))}
        onCompleted={onRefreshCapture}
      />
      <ManualNfePanel key={company.companyId} company={company}
        disabled={captureDisabled || running} onCompleted={onRefreshCapture} />
    </div>
  );
}
