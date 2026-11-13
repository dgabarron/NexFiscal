import { useEffect, useMemo, useRef, useState } from "react";
import { Building2 } from "lucide-react";
import { CompanyPicker } from "../components/operations/CompanyPicker";
import { RetroPanel } from "../components/operations/RetroPanel";
import { ZipRestorePanel } from "../components/operations/ZipRestorePanel";
import { useNotices } from "../components/common/NoticesContext";
import { pendingWindow } from "../lib/capture-utils";
import { api } from "../lib/api";
import { EmptyState } from "../components/common/EmptyState";

export function RecoveryPage({
  companies, schedules, clockNow, selectedCompanyId, onSelectCompany, onRefreshCapture,
}) {
  const { notify } = useNotices();
  const company = useMemo(
    () => companies.find((c) => c.companyId === selectedCompanyId) || companies[0],
    [companies, selectedCompanyId]
  );
  const [retroBusy, setRetroBusy] = useState({});
  const active = useRef(new Set());
  const [retroResult, setRetroResult] = useState({});
  const [catchupStatus, setCatchupStatus] = useState({});

  const companyId = company?.companyId;
  // Apenas GET de status: nenhuma chamada SEFAZ acontece no polling da interface.
  useEffect(() => {
    if (!companyId) return;
    let cancelled = false;
    async function load() {
      try {
        const job = await api.get(`/api/sefaz/nfe/catchup/${companyId}`);
        if (!cancelled) setCatchupStatus((old) => ({ ...old, [companyId]: job.status }));
      } catch {
        if (!cancelled) setCatchupStatus((old) => ({ ...old, [companyId]: null }));
      }
    }
    load();
    const timer = setInterval(load, 15000);
    return () => { cancelled = true; clearInterval(timer); };
  }, [companyId]);

  if (!company) {
    return <EmptyState icon={Building2} title="Nenhuma empresa"
      description="Cadastre uma empresa primeiro." />;
  }

  const cooldown = pendingWindow(company, clockNow);
  const nfe = schedules?.[company.companyId]?.find((s) => s.documentType === "NFE");
  const catchupRunning = catchupStatus[company.companyId] === "running";

  async function runRetro(id) {
    if (active.current.has(id)) return;
    active.current.add(id);
    setRetroBusy((prev) => ({ ...prev, [id]: true }));
    try {
      // Evita retroagir durante um catch-up ativo, inclusive quando veio de outra sessão.
      const job = await api.get(`/api/sefaz/nfe/catchup/${id}`).catch(() => null);
      if (job?.status === "running")
        throw new Error("Existe uma sincronização NF-e em andamento. Aguarde sua conclusão.");
      const result = await api.post(`/api/recovery/nfe/sefaz/${id}`, {});
      setRetroResult((prev) => ({ ...prev, [id]: result }));
      await onRefreshCapture?.();
    } catch (e) { notify.error(e.message); }
    finally {
      active.current.delete(id);
      setRetroBusy((prev) => ({ ...prev, [id]: false }));
    }
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Recuperação NF-e</h1>
        <p className="mt-1 text-sm text-slate-500">
          A consulta retroativa à SEFAZ e a restauração de ZIPs são ações independentes.
          Nenhuma delas avança o checkpoint sequencial de monitoramento.
        </p>
      </div>
      <CompanyPicker companies={companies} selectedId={company.companyId} onChange={onSelectCompany} />
      <RetroPanel key={company.companyId + "-retro"} company={company}
        cooldown={cooldown} catchupRunning={catchupRunning}
        busy={!!retroBusy[company.companyId]}
        result={retroResult[company.companyId]}
        disabled={!company.certificateStored || !!cooldown || catchupRunning ||
          !nfe?.enabled || !nfe.destinationDirectory?.trim()}
        onExecute={runRetro} />
      <ZipRestorePanel key={company.companyId} company={company} />
    </div>
  );
}
