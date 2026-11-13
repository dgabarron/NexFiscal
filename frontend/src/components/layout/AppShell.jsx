import { Sidebar } from "./Sidebar";
import { Topbar, PAGE_LABELS } from "./Topbar";
import { Notice } from "../common/Notice";
import { useNotices } from "../common/NoticesContext";
const NAV = ["overview", "companies", "captures", "recovery"];
export function AppShell({ page, onNavigate, hanaReady, refreshing, onRefresh, children }) {
  const { error, message, clear } = useNotices();
  return (
    <div className="flex min-h-screen bg-[#f5f7fb]">
      <Sidebar page={page} onNavigate={onNavigate} hanaReady={hanaReady} />
      <div className="flex min-w-0 flex-1 flex-col">
        <Topbar page={page} onNavigate={onNavigate} onRefresh={onRefresh} refreshing={refreshing} />
        <nav aria-label="Navegação principal" className="flex gap-2 overflow-x-auto px-4 pt-4 lg:hidden">
          {NAV.map((key) => (
            <button key={key} type="button" onClick={() => onNavigate(key)}
              aria-current={page === key ? "page" : undefined}
              className={`whitespace-nowrap rounded-full px-3 py-2 text-xs font-semibold ${
                page === key ? "bg-blue-600 text-white" : "border border-slate-200 bg-white text-slate-700"
              }`}>{PAGE_LABELS[key]}</button>
          ))}
        </nav>
        <main className="mx-auto w-full max-w-[1180px] px-4 py-7 sm:px-6 lg:px-10 lg:py-9">
          {(error || message) && (
            <div className="mb-5 space-y-2">
              {error && <div className="flex items-start justify-between gap-3">
                <Notice type="error">{error}</Notice>
                <button type="button" onClick={clear} className="text-xs text-slate-600 underline">Fechar</button>
              </div>}
              {message && <div className="flex items-start justify-between gap-3">
                <Notice type="success">{message}</Notice>
                <button type="button" onClick={clear} className="text-xs text-slate-600 underline">Fechar</button>
              </div>}
            </div>
          )}
          {children}
        </main>
      </div>
    </div>
  );
}
