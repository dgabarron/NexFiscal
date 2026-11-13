import { Building2, AlertTriangle, CheckCircle2 } from "lucide-react";
import { Card, CardHeader, CardContent } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Badge } from "../components/common/Badge";
import { Stat } from "../components/common/Stat";
import { EmptyState } from "../components/common/EmptyState";
import { pendingWindow, formatUtcAsLocal } from "../lib/capture-utils";
import { formatCnpj } from "../lib/fiscal-format";

export function OverviewPage({ companies, schedules, clockNow, onFocusCompany, onNavigate }) {
  if (!companies.length) return (
    <EmptyState icon={Building2} title="Nenhuma empresa cadastrada"
      description="Cadastre uma empresa com certificado A1 para iniciar."
      action={<Button onClick={() => onNavigate("companies")}>Cadastrar empresa</Button>} />
  );

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Painel</h1>
        <p className="mt-1 text-sm text-slate-500">Acompanhamento da distribuição NF-e por empresa.</p>
      </div>
      {companies.map((company) => {
        const cooldown = pendingWindow(company, clockNow);
        const configured = schedules?.[company.companyId]?.find(s => s.documentType === "NFE")?.enabled;
        const hasCertificate = !!company.certificateStored;
        const ready = hasCertificate && !!company.enabled && !!configured && !!company.cursorConfigured;
        const label = !hasCertificate ? "Certificado ausente"
          : !company.enabled ? "Empresa pausada"
          : !configured ? "Monitoramento desativado"
          : !company.cursorConfigured ? "Checkpoint pendente"
          : cooldown ? "Aguardando janela SEFAZ"
          : "Captura configurada";
        const tone = !hasCertificate || !company.cursorConfigured ? "warning"
          : ready && !cooldown ? "success" : "neutral";
        return (
          <Card key={company.companyId}>
            <CardHeader>
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <h2 className="text-lg font-bold">{company.legalName}</h2>
                  <p className="mt-1 text-sm text-slate-500">CNPJ {formatCnpj(company.cnpj)}</p>
                </div>
                <Badge tone={tone}>
                  {ready && !cooldown ? <CheckCircle2 size={13} /> : <AlertTriangle size={13} />}
                  {label}
                </Badge>
              </div>
            </CardHeader>
            <CardContent>
              <div className="grid gap-3 sm:grid-cols-3">
                <Stat label="Registros NF-e no HANA" value={company.documentsStored ?? 0} />
                <Stat label="Última tentativa NF-e" value={formatUtcAsLocal(company.lastConsultationUtc)} />
                <Stat label="Janela de consulta SEFAZ"
                  value={cooldown ? `Após ${cooldown.time} (${cooldown.minutes} min)` : "Sem bloqueio informado"}
                  tone={cooldown ? "warning" : "default"} />
              </div>
              <div className="mt-4 flex flex-wrap gap-2">
                <Button variant="outline" onClick={() => {
                  onFocusCompany(company.companyId); onNavigate("captures");
                }}>Abrir capturas</Button>
                <Button variant="ghost" onClick={() => {
                  onFocusCompany(company.companyId); onNavigate("recovery");
                }}>Recuperar documentos</Button>
              </div>
            </CardContent>
          </Card>
        );
      })}
    </div>
  );
}
