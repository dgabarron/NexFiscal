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
import { useCatchupPolling } from "../hooks/useCatchupPolling";
import { pendingWindow, formatUtcAsLocal } from "../lib/capture-utils";
import { formatCnpj } from "../lib/fiscal-format";

/**
 * Central operacional do NexFiscal: somente monitorar automaticamente e
 * consultar todos os lotes disponíveis. Sem consNSU ou restauração local.
 */
export function OverviewPage({
  companies, schedules, clockNow, selectedCompanyId, onSelectCompany,
  scheduleField, onScheduleSaved, onRefreshCapture, onNavigate,
}) {
  const [runs, setRuns] = useState({});
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
  const manualDisabled = !hasCertificate || !destinationReady || !!cooldown || running;
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
    </div>
  );
}
