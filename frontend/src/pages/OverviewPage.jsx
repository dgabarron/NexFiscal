import { useMemo, useState } from "react";
import { Building2, AlertTriangle, CheckCircle2 } from "lucide-react";
import { Card, CardHeader, CardContent } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Badge } from "../components/common/Badge";
import { Stat } from "../components/common/Stat";
import { EmptyState } from "../components/common/EmptyState";
import { Notice } from "../components/common/Notice";
import { CompanyPicker } from "../components/operations/CompanyPicker";
import { MonitorPanel } from "../components/operations/MonitorPanel";
import { CatchupPanel } from "../components/operations/CatchupPanel";
import { CtePanel } from "../components/operations/CtePanel";
import { CteSchedulePanel } from "../components/operations/CteSchedulePanel";
import { useCatchupPolling } from "../hooks/useCatchupPolling";
import { pendingWindow, formatUtcAsLocal } from "../lib/capture-utils";
import { formatCnpj } from "../lib/fiscal-format";

/**
 * Central operacional do NexFiscal: somente monitorar automaticamente e
 * consultar todos os lotes disponíveis. Sem consNSU ou restauração local.
 */
export function OverviewPage({
  companies, schedules, cteStates = {}, clockNow, selectedCompanyId, onSelectCompany,
  scheduleField, onScheduleSaved, onRefreshCapture, onNavigate,
}) {
  const [runs, setRuns] = useState({});
  const [activeType, setActiveType] = useState("NFE");
  const [cteResults, setCteResults] = useState({});
  useCatchupPolling(runs, setRuns, companies);

  const company = useMemo(
    () => companies.find((c) => c.companyId === selectedCompanyId) || companies[0],
    [companies, selectedCompanyId]
  );

  if (!company) {
    return <EmptyState icon={Building2} title="Nenhuma empresa cadastrada"
      description="Cadastre uma empresa com certificado A1 para iniciar as consultas."
      action={<Button onClick={() => onNavigate("companies")}>Cadastrar empresa</Button>} />;
  }

  const schedule = schedules[company.companyId] || [];
  const nfe = schedule.find((entry) => entry.documentType === "NFE");
  const cooldown = pendingWindow(company, clockNow);
  const run = runs[company.companyId];
  const running = run?.status === "running";
  const hasCertificate = !!company.certificateStored;
  const destinationReady = !!nfe?.destinationDirectory?.trim();
  // IMPORTANTE: a consulta manual independe do toggle do Worker.
  const manualDisabled = !hasCertificate || !destinationReady;
  const monitorConfigured = !!nfe?.enabled;
  const label = !hasCertificate ? "Certificado ausente"
    : !destinationReady ? "Configure a pasta de XMLs"
    : cooldown ? "Aguardando janela SEFAZ"
    : monitorConfigured ? "Monitoramento configurado"
    : "Consulta manual disponível";
  const badgeTone = !hasCertificate || !destinationReady ? "warning"
    : cooldown ? "neutral" : "success";

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Operação NF-e</h1>
        <p className="mt-1 text-sm text-slate-500">
          Duas operações independentes: monitorar automaticamente ou consultar os XMLs disponíveis na SEFAZ.
        </p>
      </div>
      <CompanyPicker companies={companies} selectedId={company.companyId} onChange={onSelectCompany} />
      <div role="tablist" aria-label="Documento fiscal" className="flex gap-2 border-b border-slate-200">
        {[{ type: "NFE", label: "NF-e" }, { type: "CTE", label: "CT-e" }].map((item) => (
          <button key={item.type} type="button" role="tab"
            id={`operation-tab-${item.type}`} aria-selected={activeType === item.type}
            aria-controls="operation-panel"
            onClick={() => setActiveType(item.type)}
            className={`border-b-2 px-4 py-3 text-sm font-semibold ${
              activeType === item.type ? "border-blue-600 text-blue-700" : "border-transparent text-slate-600 hover:text-slate-900"
            }`}>{item.label}</button>
        ))}
      </div>
      <section id="operation-panel" role="tabpanel" aria-labelledby={`operation-tab-${activeType}`}
        className="space-y-5">
      {activeType === "NFE" && <>
      <Card>
        <CardHeader>
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <h2 className="text-lg font-semibold">{company.legalName}</h2>
              <p className="text-sm text-slate-500">CNPJ {formatCnpj(company.cnpj)}</p>
            </div>
            <Badge tone={badgeTone}>
              {badgeTone === "success" ? <CheckCircle2 size={13} /> : <AlertTriangle size={13} />}
              {label}
            </Badge>
          </div>
        </CardHeader>
        <CardContent>
          <div className="grid gap-3 sm:grid-cols-3">
            <Stat label="Registros NF-e no HANA" value={company.documentsStored ?? 0} />
            <Stat label="Última tentativa" value={formatUtcAsLocal(company.lastConsultationUtc)} />
            <Stat label="Próxima consulta permitida"
              value={cooldown ? `Após ${cooldown.time} (${cooldown.minutes} min)` : "Sem bloqueio informado"}
              tone={cooldown ? "warning" : "default"} />
          </div>
          {company.reason && !hasCertificate && (
            <p className="mt-3 text-xs text-amber-800">{company.reason}</p>
          )}
        </CardContent>
      </Card>
      {!schedule.length && (
        <Notice type="error">Não foi possível carregar a configuração da empresa. Atualize os dados.</Notice>
      )}
      {nfe && (
        <>
          <MonitorPanel company={company} schedule={schedule}
            onScheduleField={scheduleField}
            onSaved={(result) => onScheduleSaved?.(company.companyId, result)} />
          <CatchupPanel company={company} cooldown={cooldown}
            run={run} running={running} disabled={manualDisabled}
            onStart={(result) => setRuns((prev) => ({ ...prev, [company.companyId]: result }))}
            onCompleted={onRefreshCapture} />
          {!hasCertificate && (
            <Notice type="warning">Cadastre um certificado A1 válido para consultar a SEFAZ.</Notice>
          )}
          {!destinationReady && (
            <Notice type="info">Configure e salve uma pasta de XMLs no servidor para liberar a consulta manual.</Notice>
          )}
        </>
      )}
      </>}
      {activeType === "CTE" && (
        <>
          {!schedule.length ? (
            <Notice type="error">Não foi possível carregar a configuração CT-e. Atualize os dados.</Notice>
          ) : (
            <>
              <Notice type="info">Captura manual de CT-e. O controle fiscal e o armazenamento são independentes do NF-e; não há Worker CT-e nesta versão.</Notice>
              <CteSchedulePanel company={company} schedules={schedule}
                onField={scheduleField} onSaved={onScheduleSaved} />
              <CtePanel key={company.companyId} company={company}
                cteState={cteStates[company.companyId] || {}} schedule={schedule}
                cooldown={pendingWindow(cteStates[company.companyId], clockNow)}
                result={cteResults[company.companyId]}
                onResult={async (result) => {
                  setCteResults((previous) => ({ ...previous, [company.companyId]: result }));
                  await onRefreshCapture?.();
                }} />
            </>
          )}
        </>
      )}
      </section>
    </div>
  );
}
