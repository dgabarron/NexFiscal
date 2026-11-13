import { useMemo, useState } from "react";
import { Building2 } from "lucide-react";
import { CompanyPicker } from "../components/operations/CompanyPicker";
import { CtePanel } from "../components/operations/CtePanel";
import { pendingWindow } from "../lib/capture-utils";
import { EmptyState } from "../components/common/EmptyState";

export function CteCapturePage({
  companies, cteStates, schedules, clockNow, selectedCompanyId, onSelectCompany,
}) {
  const [results, setResults] = useState({});
  const [busy] = useState(false);
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
      <CtePanel company={company} cteState={cteState} schedule={schedule} cooldown={cooldown}
        busy={busy} result={results[company.companyId]}
        onResult={(r) => setResults((prev) => ({ ...prev, [company.companyId]: r }))} />
    </div>
  );
}
