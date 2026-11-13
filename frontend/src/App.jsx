import { useState } from "react";
import { NoticesProvider } from "./components/common/NoticesContext";
import { LoadingBlock } from "./components/common/LoadingBlock";
import { Notice } from "./components/common/Notice";
import { AppShell } from "./components/layout/AppShell";
import { useBootstrap } from "./hooks/useBootstrap";
import { useCaptureData } from "./hooks/useCaptureData";
import { useClock } from "./hooks/useClock";
import { SetupPage } from "./pages/SetupPage";
import { OverviewPage } from "./pages/OverviewPage";
import { CompaniesPage } from "./pages/CompaniesPage";
import { NfeCapturePage } from "./pages/NfeCapturePage";
import { CteCapturePage } from "./pages/CteCapturePage";
import { RecoveryPage } from "./pages/RecoveryPage";
import { SettingsPage } from "./pages/SettingsPage";

export function App() {
  return (
    <NoticesProvider>
      <AppInner />
    </NoticesProvider>
  );
}

function AppInner() {
  const bootstrap = useBootstrap();
  const capture = useCaptureData();
  const clockNow = useClock();
  const [page, setPage] = useState("overview");
  const [selectedCompanyId, setSelectedCompanyId] = useState(null);

  if (bootstrap.loading || capture.loading) {
    return <div className="min-h-screen bg-[#f5f7fb]"><LoadingBlock label="Carregando NexFiscal..." /></div>;
  }

  if (bootstrap.error) {
    return (
      <div className="min-h-screen bg-[#f5f7fb] flex items-center justify-center p-6">
        <div className="max-w-lg w-full">
          <Notice type="error" title="Falha ao inicializar">
            {bootstrap.error}
            <button className="mt-3 block text-xs underline text-red-900"
              onClick={() => { bootstrap.refresh(); capture.refresh(); }}>
              Tentar novamente
            </button>
          </Notice>
        </div>
      </div>
    );
  }

  if (!bootstrap.hanaReady) {
    return <SetupPage step={0} onHanaSaved={() => bootstrap.refresh()} />;
  }
  if (capture.companies.length === 0) {
    return <SetupPage step={1} onCompanyRegistered={async () => {
      await capture.refresh();
      bootstrap.refresh();
    }} />;
  }

  const effectiveCompanyId = selectedCompanyId || capture.companies[0]?.companyId || null;

  async function refreshAll() {
    await Promise.all([bootstrap.refresh(), capture.refresh()]);
  }

  return (
    <AppShell
      page={page}
      onNavigate={setPage}
      hanaReady={bootstrap.hanaReady}
      refreshing={false}
      onRefresh={refreshAll}
    >
      {page === "overview" && (
        <OverviewPage
          companies={capture.companies}
          cteStates={capture.cteStates}
          clockNow={clockNow}
          onFocusCompany={setSelectedCompanyId}
          onNavigate={setPage}
        />
      )}
      {page === "companies" && (
        <CompaniesPage
          companies={capture.companies}
          onCompanyAdded={() => capture.refresh()}
        />
      )}
      {page === "nfe" && (
        <NfeCapturePage
          companies={capture.companies}
          cteStates={capture.cteStates}
          schedules={capture.schedules}
          clockNow={clockNow}
          selectedCompanyId={effectiveCompanyId}
          onSelectCompany={setSelectedCompanyId}
          scheduleField={capture.setScheduleField}
          onScheduleSaved={async (result) => {
            capture.replaceSchedule(effectiveCompanyId, result);
            await capture.refresh();
          }}
          onRefreshCapture={capture.refresh}
        />
      )}
      {page === "cte" && (
        <CteCapturePage
          companies={capture.companies}
          cteStates={capture.cteStates}
          schedules={capture.schedules}
          clockNow={clockNow}
          selectedCompanyId={effectiveCompanyId}
          onSelectCompany={setSelectedCompanyId}
        />
      )}
      {page === "recovery" && (
        <RecoveryPage
          companies={capture.companies}
          cteStates={capture.cteStates}
          clockNow={clockNow}
          selectedCompanyId={effectiveCompanyId}
          onSelectCompany={setSelectedCompanyId}
          onRefreshCapture={capture.refresh}
        />
      )}
      {page === "settings" && <SettingsPage hanaReady={bootstrap.hanaReady} />}
    </AppShell>
  );
}
