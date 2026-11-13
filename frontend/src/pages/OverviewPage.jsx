import { Building2, CheckCircle2, AlertTriangle, Lock, Plug } from "lucide-react";
import { Card, CardHeader, CardContent } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Badge } from "../components/common/Badge";
import { Stat } from "../components/common/Stat";
import { EmptyState } from "../components/common/EmptyState";
import { Notice } from "../components/common/Notice";
import { useNotices } from "../components/common/NoticesContext";
import { api } from "../lib/api";
import { pendingWindow, formatUtcAsLocal } from "../lib/capture-utils";
import { formatCnpj } from "../lib/fiscal-format";

export function OverviewPage({ companies, cteStates, schedules, clockNow, onFocusCompany, onNavigate }) {
  const { notify } = useNotices();

  async function testConnectivity(companyId) {
    try {
      const r = await api.post("/api/capture/test-connectivity", { companyId });
      if (r.reachable) notify.success("Conexão HTTPS com a SEFAZ confirmada.");
      else notify.error(r.message || "Conexão SEFAZ indisponível.");
    } catch (e) { notify.error(e.message); }
  }

  if (companies.length === 0) {
    return (
      <EmptyState icon={Building2} title="Nenhuma empresa cadastrada"
        description="Cadastre a primeira empresa pelo certificado A1 para começar a capturar documentos."
        action={<Button onClick={() => onNavigate("companies")}>Ir para Empresas</Button>} />
    );
  }

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Central de operação</h1>
        <p className="text-sm text-slate-500 mt-1">
          Estado atual das empresas monitoradas. Cada card resume monitoramento, capturas e
          próxima janela fiscal.
        </p>
      </div>
      {companies.map((company) => {
        const cooldown = pendingWindow(company, clockNow);
        const cteState = cteStates[company.companyId] || {};
        const cteCooldown = pendingWindow(cteState, clockNow);
        const certOk = !!company.certificateStored;
        const nfeSchedule = schedules?.[company.companyId]?.find((s) => s.documentType === "NFE");
        const monitoringActive = !!nfeSchedule?.enabled;
        return (
          <Card key={company.companyId}>
            <CardHeader>
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                  <h3 className="text-lg font-bold">{company.legalName}</h3>
                  <p className="mt-0.5 text-sm text-slate-500">CNPJ {formatCnpj(company.cnpj)}</p>
                </div>
                <div className="flex items-center gap-2">
                  {!certOk ? <Badge tone="warning"><AlertTriangle size={13} />Certificado ausente</Badge>
                    : !monitoringActive ? <Badge tone="neutral">Monitoramento desativado</Badge>
                    : cooldown ? <Badge tone="warning">Aguardando cooldown</Badge>
                    : <Badge tone="success"><CheckCircle2 size={13} />Monitoramento configurado</Badge>}
                </div>
              </div>
            </CardHeader>
            <CardContent>
              <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                <Stat label="XMLs armazenados" value={company.documentsStored ?? 0} />
                <Stat label="Última consulta NF-e" value={formatUtcAsLocal(company.lastConsultationUtc)} />
                <Stat label="Próxima consulta NF-e"
                  value={cooldown ? `Após ${cooldown.time} (${cooldown.minutes} min)` : certOk ? "Disponível" : "Aguardando certificado"}
                  tone={cooldown ? "warning" : "success"} />
                <Stat label="Próxima consulta CT-e"
                  value={cteCooldown ? `Após ${cteCooldown.time}` : cteState.nextAllowedUtc ? "Disponível" : "—"} />
              </div>
              {cooldown && (
                <div className="mt-3"><Notice>
                  Cooldown NF-e ativo até {cooldown.time}. A UI não contorna{" "}
                  <code className="text-xs">NextAllowedAtUtc</code>.
                </Notice></div>
              )}
              <div className="mt-4 grid gap-2 sm:grid-cols-3">
                <Button variant="outline" onClick={() => { onFocusCompany(company.companyId); onNavigate("nfe"); }}>
                  Ver captura NF-e
                </Button>
                <Button variant="outline" onClick={() => { onFocusCompany(company.companyId); onNavigate("recovery"); }}>
                  Ver recuperação
                </Button>
                <Button variant="ghost" onClick={() => testConnectivity(company.companyId)}>
                  <Plug size={16} />Testar conexão SEFAZ
                </Button>
              </div>
            </CardContent>
          </Card>
        );
      })}
      <div className="rounded-xl border border-slate-200 bg-white p-4 text-xs text-slate-500 flex items-start gap-2">
        <Lock size={14} className="mt-0.5" />
        <span>O portal é acessível apenas localmente (loopback) nesta versão. Autenticação
          multiusuário e exposição em rede exigem implementação futura no backend.</span>
      </div>
    </div>
  );
}
