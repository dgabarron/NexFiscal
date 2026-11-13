import { ChevronRight, RefreshCw, Settings, ArrowLeft } from "lucide-react";
export const PAGE_LABELS = {
  overview: "Painel", companies: "Empresas", captures: "Capturas",
  recovery: "Recuperação", settings: "Configuração técnica",
};
export function Topbar({ page, onRefresh, onNavigate, refreshing }) {
  return (
    <header className="flex min-h-[72px] items-center justify-between gap-3 border-b border-slate-200 bg-white px-4 sm:px-6 lg:px-10">
      <div className="flex min-w-0 items-center gap-2 text-sm">
        <span className="hidden text-slate-400 sm:inline">NexFiscal</span>
        <ChevronRight size={14} className="hidden text-slate-300 sm:inline" />
        <strong className="truncate text-slate-800">{PAGE_LABELS[page] || page}</strong>
      </div>
      <div className="flex shrink-0 items-center gap-2">
        <button type="button" onClick={onRefresh} disabled={refreshing}
          className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-50">
          <RefreshCw size={15} className={refreshing ? "animate-spin" : ""} />
          <span className="hidden sm:inline">{refreshing ? "Atualizando" : "Atualizar"}</span>
        </button>
        <button type="button" onClick={() => onNavigate(page === "settings" ? "overview" : "settings")}
          aria-label={page === "settings" ? "Voltar ao painel" : "Abrir configuração técnica"}
          title={page === "settings" ? "Voltar ao painel" : "Configuração técnica"}
          className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50">
          {page === "settings" ? <ArrowLeft size={15} /> : <Settings size={15} />}
          <span className="hidden sm:inline">{page === "settings" ? "Voltar ao painel" : "Configuração técnica"}</span>
        </button>
      </div>
    </header>
  );
}
