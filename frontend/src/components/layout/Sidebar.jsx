import { FileText, LayoutDashboard, Building2, DownloadCloud, Truck, History, Settings } from "lucide-react";

const NAV = [
  { key: "overview",  label: "Visão geral",   icon: LayoutDashboard },
  { key: "companies", label: "Empresas",      icon: Building2 },
  { key: "nfe",       label: "Captura NF-e",  icon: DownloadCloud },
  { key: "cte",       label: "Captura CT-e",  icon: Truck },
  { key: "recovery",  label: "Recuperação",   icon: History },
  { key: "settings",  label: "Configurações", icon: Settings },
];

export function Sidebar({ page, onNavigate, hanaReady }) {
  return (
    <aside className="hidden lg:flex w-[260px] flex-col bg-[#101d35] text-white shrink-0">
      <div className="flex items-center gap-3 px-6 h-[80px] border-b border-white/10">
        <div className="rounded-xl bg-blue-500/20 p-2.5 text-blue-300"><FileText size={22} /></div>
        <div>
          <div className="font-bold text-[19px]">NexFiscal</div>
          <div className="text-[10px] uppercase tracking-[0.17em] text-slate-400">Plataforma fiscal</div>
        </div>
      </div>
      <nav className="flex-1 px-4 pt-6 space-y-1">
        {NAV.map(({ key, label, icon: Icon }) => (
          <button key={key} onClick={() => onNavigate(key)}
            aria-current={page === key ? "page" : undefined}
            className={`w-full flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm text-left transition ${
              page === key ? "bg-blue-600 text-white" : "text-slate-300 hover:bg-white/5 hover:text-white"
            }`}>
            <Icon size={18} /><span className="font-medium">{label}</span>
          </button>
        ))}
      </nav>
      <div className="px-4 pb-5">
        <div className={`rounded-lg border p-3 text-xs ${
          hanaReady ? "border-emerald-400/30 bg-emerald-400/10 text-emerald-200"
                    : "border-amber-400/30 bg-amber-400/10 text-amber-200"
        }`}>
          {hanaReady ? "Infraestrutura configurada" : "Instalação pendente"}
        </div>
        <p className="mt-3 text-[10px] text-slate-500">NexFiscal · v0.3</p>
      </div>
    </aside>
  );
}
