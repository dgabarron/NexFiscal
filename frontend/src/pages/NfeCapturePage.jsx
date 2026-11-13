import { useMemo, useState } from "react";
import { Building2 } from "lucide-react";
import { CompanyPicker } from "../components/operations/CompanyPicker";
import { MonitorPanel } from "../components/operations/MonitorPanel";
import { CatchupPanel } from "../components/operations/CatchupPanel";
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
  useCatchupPolling(runs, setRuns);

  if (!company) {
    return <EmptyState icon={Building2} title="Nenhuma empresa" description="Cadastre uma empresa primeiro." />;
  }
  const run = runs[company.companyId];
  const running = run?.status === "running";

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
      <MonitorPanel company={company} schedule={schedule} onScheduleField={scheduleField} onSaved={onScheduleSaved} />
      <CatchupPanel
        company={company}
        cooldown={cooldown}
        run={run}
        running={running}
        disabled={!company.certificateStored || !!cooldown}
        onStart={(result) => setRuns((prev) => ({ ...prev, [company.companyId]: result }))}
        onCompleted={onRefreshCapture}
      />
    </div>
  );
}
