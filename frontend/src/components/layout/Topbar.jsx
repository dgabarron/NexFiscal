import { ChevronRight, RefreshCw } from "lucide-react";

const LABELS = {
  overview: "Visão geral", companies: "Empresas", nfe: "Captura NF-e",
  cte: "Captura CT-e", recovery: "Recuperação", settings: "Configurações",
};

export function Topbar({ page, onRefresh, refreshing }) {
  return (
    <header className="flex min-h-[80px] items-center justify-between border-b border-slate-200 bg-white px-6 lg:px-10">
      <div className="flex items-center gap-2 text-sm">
        <span className="text-slate-400">NexFiscal</span>
        <ChevronRight size={14} className="text-slate-300" />
        <span className="font-semibold text-slate-800">{LABELS[page] || page}</span>
      </div>
      <button type="button" onClick={onRefresh} disabled={refreshing}
        className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-50">
        <RefreshCw size={14} className={refreshing ? "animate-spin" : ""} />Atualizar
      </button>
    </header>
  );
}
