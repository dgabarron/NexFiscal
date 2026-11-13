export function EmptyState({ icon: Icon, title, description, action }) {
  return (
    <div className="rounded-xl border border-dashed border-slate-200 bg-white p-10 text-center">
      {Icon && <Icon size={32} className="mx-auto text-slate-300" />}
      <h3 className="mt-4 font-semibold text-slate-800">{title}</h3>
      {description && <p className="mt-1 text-sm text-slate-500 max-w-md mx-auto">{description}</p>}
      {action && <div className="mt-5">{action}</div>}
    </div>
  );
}
