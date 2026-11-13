import { Sidebar } from "./Sidebar";
import { Topbar } from "./Topbar";
import { Notice } from "../common/Notice";
import { useNotices } from "../common/NoticesContext";

export function AppShell({ page, onNavigate, hanaReady, refreshing, onRefresh, children }) {
  const { error, message, clear } = useNotices();
  return (
    <div className="flex min-h-screen bg-[#f5f7fb]">
      <Sidebar page={page} onNavigate={onNavigate} hanaReady={hanaReady} />
      <div className="min-w-0 flex-1 flex flex-col">
        <Topbar page={page} onRefresh={onRefresh} refreshing={refreshing} />
        <div className="lg:hidden px-4 pt-4">
          <nav className="flex gap-2 overflow-x-auto pb-2">
            {["overview","companies","nfe","cte","recovery","settings"].map((key) => (
              <button key={key} onClick={() => onNavigate(key)}
                className={`whitespace-nowrap rounded-full px-3 py-1.5 text-xs font-semibold ${
                  page === key ? "bg-blue-600 text-white" : "bg-white text-slate-600 border border-slate-200"
                }`}>{key}</button>
            ))}
          </nav>
        </div>
        <main className="mx-auto w-full max-w-[1180px] px-4 py-8 sm:px-6 lg:px-10 lg:py-10">
          {(error || message) && (
            <div className="mb-5 flex flex-col gap-3">
              {error && (
                <div className="flex items-start gap-2">
                  <Notice type="error">{error}</Notice>
                  <button onClick={clear} className="text-xs text-slate-500 hover:text-slate-800 self-center">fechar</button>
                </div>
              )}
              {message && (
                <div className="flex items-start gap-2">
                  <Notice type="success">{message}</Notice>
                  <button onClick={clear} className="text-xs text-slate-500 hover:text-slate-800 self-center">fechar</button>
                </div>
              )}
            </div>
          )}
          {children}
        </main>
      </div>
    </div>
  );
}
