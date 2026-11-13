import { AlertCircle, CheckCircle2 } from "lucide-react";

const VARIANTS = {
  error:   "bg-red-50 border-red-200 text-red-800",
  success: "bg-emerald-50 border-emerald-200 text-emerald-800",
  warning: "bg-amber-50 border-amber-200 text-amber-900",
  info:    "bg-slate-50 border-slate-200 text-slate-700",
};

export function Notice({ type = "warning", title, children }) {
  return (
    <div role={type === "error" ? "alert" : "status"}
      className={`flex items-start gap-3 rounded-xl border p-3.5 text-sm leading-relaxed ${VARIANTS[type] || VARIANTS.warning}`}>
      <span className="mt-0.5 shrink-0">
        {type === "success" ? <CheckCircle2 size={17} /> : <AlertCircle size={17} />}
      </span>
      <div className="min-w-0">
        {title && <div className="font-semibold mb-1">{title}</div>}
        {children}
      </div>
    </div>
  );
}
