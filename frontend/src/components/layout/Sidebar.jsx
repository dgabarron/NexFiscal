import { FileText, LayoutDashboard, Building2, DownloadCloud, History } from "lucide-react";
const NAV = [
  { key: "overview", label: "Painel", icon: LayoutDashboard },
  { key: "companies", label: "Empresas", icon: Building2 },
  { key: "captures", label: "Capturas", icon: DownloadCloud },
  { key: "recovery", label: "Recuperação", icon: History },
];
export function Sidebar({ page, onNavigate, hanaReady }) {
  return (
    <aside className="hidden lg:flex w-[248px] shrink-0 flex-col bg-[#101d35] text-white">
      <div className="flex h-[80px] items-center gap-3 border-b border-white/10 px-6">
        <div className="rounded-xl bg-blue-500/20 p-2.5 text-blue-300"><FileText size={21} /></div>
        <div>
          <div className="text-lg font-bold">NexFiscal</div>
          <div className="text-[10px] uppercase tracking-wider text-slate-400">Plataforma fiscal</div>
        </div>
      </div>
      <nav aria-label="Navegação principal" className="flex-1 space-y-1 px-4 pt-6">
        {NAV.map(({ key, label, icon: Icon }) => (
          <button key={key} type="button" onClick={() => onNavigate(key)}
            aria-current={page === key ? "page" : undefined}
            className={`flex w-full items-center gap-3 rounded-lg px-3 py-2.5 text-left text-sm ${
              page === key ? "bg-blue-600 text-white" : "text-slate-300 hover:bg-white/10 hover:text-white"
            }`}>
            <Icon size={18} />{label}
          </button>
        ))}
      </nav>
      <div className="px-4 pb-5 text-xs text-slate-400">
        {hanaReady ? "Infraestrutura configurada" : "Instalação pendente"}
      </div>
    </aside>
  );
}
