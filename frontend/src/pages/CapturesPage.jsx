import { useMemo, useState } from "react";
import { Building2 } from "lucide-react";
import { CompanyPicker } from "../components/operations/CompanyPicker";
import { MonitorPanel } from "../components/operations/MonitorPanel";
import { CatchupPanel } from "../components/operations/CatchupPanel";
import { CteSchedulePanel } from "../components/operations/CteSchedulePanel";
import { CtePanel } from "../components/operations/CtePanel";
import { Notice } from "../components/common/Notice";
import { EmptyState } from "../components/common/EmptyState";
import { useCatchupPolling } from "../hooks/useCatchupPolling";
import { pendingWindow } from "../lib/capture-utils";

/**
 * Capturas mantém NF-e e CT-e em abas, sem misturar cursores ou chamadas fiscais.
 * A consulta manual de um único lote NF-e permanece no backend para diagnóstico.
 */
export function CapturesPage({
  companies, cteStates, schedules, clockNow, selectedCompanyId, onSelectCompany,
  scheduleField, onScheduleSaved, onRefreshCapture,
}) {
  const [type, setType] = useState("NFE");
  const [runs, setRuns] = useState({});
  const [cteResults, setCteResults] = useState({});
  useCatchupPolling(runs, setRuns, companies);

  const company = useMemo(
    () => companies.find((c) => c.companyId === selectedCompanyId) || companies[0],
    [companies, selectedCompanyId]
  );

  if (!company) {
    return <EmptyState icon={Building2} title="Nenhuma empresa"
      description="Cadastre uma empresa para iniciar a captura." />;
  }

  const schedule = schedules[company.companyId] || [];
  const nfeConfig = schedule.find((s) => s.documentType === "NFE");
  const nfeCooldown = pendingWindow(company, clockNow);
  const nfeRun = runs[company.companyId];
  const nfeDisabled = !company.certificateStored || !!nfeCooldown ||
    !nfeConfig?.enabled || !nfeConfig.destinationDirectory?.trim();

  const cteState = cteStates[company.companyId] || {};
  const cteCooldown = pendingWindow(cteState, clockNow);

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Capturas</h1>
        <p className="mt-1 text-sm text-slate-500">
          Configure e acompanhe a distribuição dos documentos fiscais por empresa.
        </p>
      </div>
      <CompanyPicker companies={companies} selectedId={company.companyId} onChange={onSelectCompany} />
      <div role="tablist" aria-label="Tipo de documento" className="flex gap-2 border-b border-slate-200">
        {[{ key: "NFE", label: "NF-e" }, { key: "CTE", label: "CT-e" }].map((item) => (
          <button key={item.key} role="tab" type="button"
            id={`capture-tab-${item.key}`}
            aria-selected={type === item.key}
            aria-controls="capture-tab-panel"
            onClick={() => setType(item.key)}
            className={`px-4 py-3 text-sm font-semibold border-b-2 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 ${
              type === item.key ? "border-blue-600 text-blue-700" : "border-transparent text-slate-500 hover:text-slate-900"
            }`}>
            {item.label}
          </button>
        ))}
      </div>
      <section role="tabpanel" id="capture-tab-panel" aria-labelledby={`capture-tab-${type}`}
        className="space-y-5">
        {schedule.length === 0 ? (
          <Notice type="error">Não foi possível carregar as configurações. Atualize os dados.</Notice>
        ) : type === "NFE" ? (
          <>
            <MonitorPanel company={company} schedule={schedule}
              onScheduleField={scheduleField}
              onSaved={(result) => onScheduleSaved?.(company.companyId, result)} />
            <CatchupPanel company={company} cooldown={nfeCooldown}
              run={nfeRun} running={nfeRun?.status === "running"}
              disabled={nfeDisabled} 
              onStart={(result) => setRuns((prev) => ({ ...prev, [company.companyId]: result }))}
              onCompleted={onRefreshCapture} />
          </>
        ) : (
          <>
            <Notice type="info">A captura CT-e é manual nesta versão. O NSU CT-e é independente do NF-e.</Notice>
            <CteSchedulePanel company={company} schedules={schedule}
              onField={scheduleField} onSaved={onScheduleSaved} />
            <CtePanel key={company.companyId} company={company} cteState={cteState}
              schedule={schedule} cooldown={cteCooldown}
              result={cteResults[company.companyId]}
              onResult={async (result) => {
                setCteResults((prev) => ({ ...prev, [company.companyId]: result }));
                await onRefreshCapture?.();
              }} />
          </>
        )}
      </section>
    </div>
  );
}
