export function Stat({ label, value, tone = "default", mono = false }) {
  const cls = tone === "warning" ? "text-amber-800" : tone === "success" ? "text-emerald-800" : "text-slate-900";
  return (
    <div className="rounded-lg bg-slate-50 p-4">
      <p className="text-xs text-slate-500">{label}</p>
      <p className={`mt-2 text-sm font-semibold ${mono ? "font-mono" : ""} ${cls}`}>{value}</p>
    </div>
  );
}
