import { useMemo, useState } from "react";
import { Building2 } from "lucide-react";
import { CompanyPicker } from "../components/operations/CompanyPicker";
import { RetroPanel } from "../components/operations/RetroPanel";
import { ZipRestorePanel } from "../components/operations/ZipRestorePanel";
import { useNotices } from "../components/common/NoticesContext";
import { pendingWindow } from "../lib/capture-utils";
import { api } from "../lib/api";
import { EmptyState } from "../components/common/EmptyState";

export function RecoveryPage({
  companies, cteStates, clockNow, selectedCompanyId, onSelectCompany, onRefreshCapture,
}) {
  const { notify } = useNotices();
  const company = useMemo(
    () => companies.find((c) => c.companyId === selectedCompanyId) || companies[0],
    [companies, selectedCompanyId]
  );
  const [retroBusy, setRetroBusy] = useState(false);
  const [retroResult, setRetroResult] = useState({});
  const [zipPreviews, setZipPreviews] = useState({});
  const [zipResults, setZipResults] = useState({});
  const [zipBusy] = useState(false);

  if (!company) {
    return <EmptyState icon={Building2} title="Nenhuma empresa" description="Cadastre uma empresa primeiro." />;
  }
  const cooldown = pendingWindow(company, clockNow);

  async function runRetro() {
    if (!window.confirm(
      "Consultar pontualmente uma lacuna de NSU conhecida na SEFAZ?\n\n" +
      "O checkpoint de monitoramento NÃO será alterado."
    )) return;
    setRetroBusy(true);
    try {
      const result = await api.post(`/api/recovery/nfe/sefaz/${company.companyId}`, {});
      setRetroResult((prev) => ({ ...prev, [company.companyId]: result }));
      onRefreshCapture?.();
    } catch (e) { notify.error(e.message); }
    finally { setRetroBusy(false); }
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Recuperação NF-e</h1>
        <p className="text-sm text-slate-500 mt-1">
          Duas ações separadas e complementares ao monitoramento automático. Nenhuma delas
          avança o checkpoint do monitor.
        </p>
      </div>
      <CompanyPicker companies={companies} selectedId={company.companyId} onChange={onSelectCompany} />
      <RetroPanel company={company} cooldown={cooldown} busy={retroBusy}
        result={retroResult[company.companyId]}
        disabled={!company.certificateStored || !!cooldown}
        onStart={(result) => setRetroResult((prev) => ({ ...prev, [company.companyId]: result }))} />
      <ZipRestorePanel company={company}
        preview={zipPreviews[company.companyId]}
        restoreResult={zipResults[company.companyId]}
        onPreview={(data) => setZipPreviews((prev) => ({ ...prev, [company.companyId]: data }))}
        onRestore={(result) => setZipResults((prev) => ({ ...prev, [company.companyId]: result }))}
        busy={zipBusy} />
    </div>
  );
}
