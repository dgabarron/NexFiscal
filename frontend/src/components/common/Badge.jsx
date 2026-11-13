const TONES = {
  neutral: "bg-slate-100 text-slate-700",
  success: "bg-emerald-50 text-emerald-800",
  warning: "bg-amber-50 text-amber-800",
  danger:  "bg-red-50 text-red-800",
  info:    "bg-blue-50 text-blue-800",
};

export function Badge({ tone = "neutral", children }) {
  return (
    <span className={`inline-flex items-center gap-1 rounded-full px-3 py-1 text-xs font-semibold ${TONES[tone] || TONES.neutral}`}>
      {children}
    </span>
  );
}
