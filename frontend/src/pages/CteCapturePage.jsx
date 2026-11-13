import { useMemo, useState } from "react";
import { CteSchedulePanel } from "../components/operations/CteSchedulePanel";
import { Notice } from "../components/common/Notice";
import { Building2 } from "lucide-react";
import { CompanyPicker } from "../components/operations/CompanyPicker";
import { CtePanel } from "../components/operations/CtePanel";
import { pendingWindow } from "../lib/capture-utils";
import { EmptyState } from "../components/common/EmptyState";

export function CteCapturePage({
  companies, cteStates, schedules, clockNow, selectedCompanyId, onSelectCompany,
  scheduleField, onScheduleSaved, onRefreshCapture,
}) {
  const [results, setResults] = useState({});

  const company = useMemo(
    () => companies.find((c) => c.companyId === selectedCompanyId) || companies[0],
    [companies, selectedCompanyId]
  );
  if (!company) {
    return <EmptyState icon={Building2} title="Nenhuma empresa" description="Cadastre uma empresa primeiro." />;
  }
  const cteState = cteStates[company.companyId] || {};
  const cooldown = pendingWindow(cteState, clockNow);
  const schedule = schedules[company.companyId] || [];
  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Captura CT-e</h1>
        <p className="text-sm text-slate-500 mt-1">
          Consulta manual. O cursor CT-e é independente do cursor NF-e. O Worker automático
          de CT-e ainda não está disponível nesta versão.
        </p>
      </div>
      <CompanyPicker companies={companies} selectedId={company.companyId} onChange={onSelectCompany} />
      {schedule.length === 0 ? <Notice type="error">Não foi possível carregar a configuração CT-e. Atualize os dados.</Notice>
        : <CteSchedulePanel company={company} schedules={schedule}
          onField={scheduleField} onSaved={onScheduleSaved} />}
      <CtePanel company={company} cteState={cteState} schedule={schedule} cooldown={cooldown}
        result={results[company.companyId]}
        onResult={async (r) => {
          setResults((prev) => ({ ...prev, [company.companyId]: r }));
          await onRefreshCapture?.();
        }} />
    </div>
  );
}
