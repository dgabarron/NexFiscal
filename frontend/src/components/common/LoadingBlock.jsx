import { Loader2 } from "lucide-react";

export function LoadingBlock({ label = "Carregando..." }) {
  return (
    <div className="flex items-center justify-center gap-2 py-12 text-sm text-slate-500">
      <Loader2 size={18} className="animate-spin" />{label}
    </div>
  );
}
