export function CompanyPicker({ companies, selectedId, onChange }) {
  if (!companies || companies.length <= 1) return null;
  return (
    <div className="mb-6">
      <label htmlFor="company-picker" className="mb-1 block text-xs font-semibold text-slate-600">Empresa em foco</label>
      <select id="company-picker" value={selectedId || ""} onChange={(e) => onChange(e.target.value)}
        className="w-full max-w-md rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
        {companies.map((c) => <option key={c.companyId} value={c.companyId}>{c.legalName}</option>)}
      </select>
    </div>
  );
}
