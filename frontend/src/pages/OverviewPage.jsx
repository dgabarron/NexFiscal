import { useMemo, useState } from "react";
import { Activity, AlertTriangle, Building2, CheckCircle2, FileCheck2, History, Truck } from "lucide-react";
import { Card, CardContent } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Badge } from "../components/common/Badge";
import { EmptyState } from "../components/common/EmptyState";
import { Notice } from "../components/common/Notice";
import { CompanyPicker } from "../components/operations/CompanyPicker";
import { MonitorPanel } from "../components/operations/MonitorPanel";
import { CatchupPanel } from "../components/operations/CatchupPanel";
import { NfeKeyDiagnosticsPanel } from "../components/operations/NfeKeyDiagnosticsPanel";
import { useCatchupPolling } from "../hooks/useCatchupPolling";
import { pendingWindow, formatUtcAsLocal } from "../lib/capture-utils";
import { formatCnpj } from "../lib/fiscal-format";

function InfoMetric({ icon: Icon, label, value, detail }) {
  return (
    <div className="flex items-start gap-3 rounded-xl border border-slate-200 bg-white p-4">
      <div className="rounded-lg bg-slate-100 p-2 text-slate-600"><Icon size={18} /></div>
      <div className="min-w-0">
        <p className="text-xs font-medium text-slate-500">{label}</p>
        <p className="mt-1 text-sm font-bold text-slate-900">{value}</p>
        {detail && <p className="mt-1 text-xs text-slate-500">{detail}</p>}
      </div>
    </div>
  );
}

export function OverviewPage({
  companies, schedules, cteStates = {}, clockNow, selectedCompanyId, onSelectCompany,
  scheduleField, onScheduleSaved, onRefreshCapture, onNavigate,
}) {
  const [runs, setRuns] = useState({});
  const [documentType, setDocumentType] = useState("NFE");
  const [cteRuns, setCteRuns] = useState({});
  useCatchupPolling(runs, setRuns, companies);
  useCatchupPolling(cteRuns, setCteRuns, companies, "CTE");

  const company = useMemo(
    () => companies.find((item) => item.companyId === selectedCompanyId) || companies[0],
    [companies, selectedCompanyId]
  );
  if (!company) return (
    <EmptyState icon={Building2} title="Cadastre sua primeira empresa"
      description="Adicione o certificado digital A1 para começar a receber os documentos fiscais."
      action={<Button onClick={() => onNavigate("companies")}>Cadastrar empresa</Button>} />
  );

  const configured = schedules[company.companyId] || [];
  const nfe = configured.find((item) => item.documentType === "NFE");
  const cte = configured.find((item) => item.documentType === "CTE");
  const nfeWindow = pendingWindow(company, clockNow);
  const cteState = cteStates[company.companyId] || {};
  const cteWindow = pendingWindow(cteState, clockNow);
  const run = runs[company.companyId];
  const cteRun = cteRuns[company.companyId];
  const hasCertificate = !!company.certificateStored;
  const nfeFolder = !!nfe?.destinationDirectory?.trim();
  const cteFolder = !!cte?.destinationDirectory?.trim();

  const status = !hasCertificate ? { label: "Certificado pendente", tone: "warning" }
    : documentType === "NFE" && !nfeFolder ? { label: "Configure a pasta", tone: "warning" }
    : documentType === "NFE" && run?.status === "scheduled" ? { label: "Consulta agendada", tone: "info" }
    : documentType === "NFE" && run?.status === "running" ? { label: "Capturando NF-e", tone: "info" }
    : documentType === "NFE" && nfeWindow ? { label: "Janela fiscal em espera", tone: "neutral" }
    : documentType === "CTE" && !cteFolder ? { label: "Configure a pasta CT-e", tone: "warning" }
    : documentType === "CTE" && cteRun?.status === "scheduled" ? { label: "Captura CT-e agendada", tone: "info" }
    : documentType === "CTE" && cteRun?.status === "running" ? { label: "Capturando CT-e", tone: "info" }
    : documentType === "CTE" && cteWindow ? { label: "Janela fiscal em espera", tone: "neutral" }
    : { label: "Pronto para operar", tone: "success" };

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <div className="mb-1 text-xs font-bold uppercase tracking-widest text-blue-700">Central de operação</div>
          <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">Captura de documentos fiscais</h1>
          <p className="mt-2 text-sm text-slate-500">Acompanhe e consulte os documentos disponíveis para sua empresa.</p>
        </div>
        <Badge tone={status.tone}>
          {status.tone === "success" ? <CheckCircle2 size={14} /> : <AlertTriangle size={14} />}
          {status.label}
        </Badge>
      </div>

      <div className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <p className="text-xs font-semibold uppercase tracking-wider text-slate-500">Empresa selecionada</p>
            <p className="mt-1 text-base font-bold text-slate-900">{company.legalName}</p>
            <p className="mt-1 text-sm text-slate-500">CNPJ {formatCnpj(company.cnpj)}</p>
          </div>
          <CompanyPicker companies={companies} selectedId={company.companyId} onChange={onSelectCompany} />
        </div>
      </div>

      <div role="tablist" aria-label="Tipo de documento" className="flex gap-2 rounded-xl bg-slate-100 p-1.5">
        {[
          { key: "NFE", label: "Notas fiscais (NF-e)", icon: FileCheck2 },
          { key: "CTE", label: "Conhecimentos (CT-e)", icon: Truck },
        ].map(({ key, label, icon: Icon }) => (
          <button key={key} type="button" role="tab"
            id={`tab-${key}`} aria-controls="capture-content" aria-selected={documentType === key}
            onClick={() => setDocumentType(key)}
            className={`flex min-w-0 flex-1 items-center justify-center gap-2 rounded-lg px-3 py-3 text-sm font-semibold transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 ${
              documentType === key ? "bg-white text-blue-700 shadow-sm" : "text-slate-600 hover:text-slate-900"
            }`}>
            <Icon size={17} />{label}
          </button>
        ))}
      </div>

      <section role="tabpanel" id="capture-content" aria-labelledby={`tab-${documentType}`}
        className="space-y-5">
        {documentType === "NFE" ? (
          <>
            <div className="grid gap-3 md:grid-cols-3">
              <InfoMetric icon={FileCheck2} label="Registros recebidos no HANA"
                value={(company.documentsStored ?? 0).toLocaleString("pt-BR")}
                detail="Inclui XMLs completos, resumos e eventos" />
              <InfoMetric icon={History} label="Última tentativa"
                value={formatUtcAsLocal(company.lastConsultationUtc)} />
              <InfoMetric icon={Activity} label="Janela de consulta"
                value={nfeWindow ? `Liberada após ${nfeWindow.time}` : "Sem bloqueio informado"}
                detail={nfeWindow ? "Você pode solicitar agora; a captura aguardará a liberação" : undefined} />
            </div>

            {!configured.length ? (
              <Notice type="error">Não foi possível carregar as configurações. Clique em Atualizar.</Notice>
            ) : (
              <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1.15fr)_minmax(340px,0.85fr)]">
                <div className="space-y-4">
                  <CatchupPanel company={company} cooldown={nfeWindow}
                    run={run} running={run?.status === "running" || run?.status === "scheduled"}
                    disabled={!hasCertificate || !nfeFolder}
                    onStart={(result) => setRuns((prev) => ({ ...prev, [company.companyId]: result }))}
                    onCompleted={onRefreshCapture} />
                  <NfeKeyDiagnosticsPanel key={company.companyId} company={company} />
                </div>
                <MonitorPanel company={company} schedule={configured}
                  onScheduleField={scheduleField}
                  onSaved={(result) => onScheduleSaved?.(company.companyId, result)} />
              </div>
            )}
            {!hasCertificate && <Notice type="warning">O certificado A1 da empresa precisa estar cadastrado.</Notice>}
            {!nfeFolder && configured.length > 0 &&
              <Notice type="info">Configure a pasta de XMLs no monitoramento e salve antes de capturar.</Notice>}
          </>
        ) : (
          <>
            <div className="grid gap-3 md:grid-cols-3">
              <InfoMetric icon={Truck} label="Monitoramento CT-e" value={cte?.enabled ? "Ativo" : "Desativado"} />
              <InfoMetric icon={History} label="Última consulta" value={formatUtcAsLocal(cteState.lastAttemptUtc)} />
              <InfoMetric icon={Activity} label="Janela de consulta"
                value={cteWindow ? `Liberada após ${cteWindow.time}` : "Sem bloqueio informado"}
                detail={cteWindow ? "Solicite agora para agendar automaticamente" : undefined} />
            </div>
            {!configured.length ? (
              <Notice type="error">Não foi possível carregar as configurações CT-e. Clique em Atualizar.</Notice>
            ) : (
              <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1.15fr)_minmax(340px,0.85fr)]">
                <CatchupPanel company={company} documentType="CTE" cooldown={cteWindow}
                  run={cteRun} running={cteRun?.status === "running" || cteRun?.status === "scheduled"}
                  disabled={!hasCertificate || !cteFolder}
                  onStart={(result) => setCteRuns((prev) => ({ ...prev, [company.companyId]: result }))}
                  onCompleted={onRefreshCapture} />
                <MonitorPanel company={company} documentType="CTE" schedule={configured}
                  onScheduleField={scheduleField}
                  onSaved={(result) => onScheduleSaved?.(company.companyId, result)} />
              </div>
            )}
            {!hasCertificate && <Notice type="warning">Cadastre o certificado A1 para consultar CT-e.</Notice>}
            {!cteFolder && configured.length > 0 &&
              <Notice type="info">Configure e salve a pasta dos XMLs CT-e para capturar.</Notice>}
          </>
        )}
      </section>
    </div>
  );
}
