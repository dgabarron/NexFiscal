import { useState } from "react";
import { Search, FileCheck2 } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Notice } from "../common/Notice";
import { api } from "../../lib/api";

const LABELS = {
  COMPLETE: ["Completa", "success"],
  MISSING_FILE: ["XML ausente da pasta", "warning"],
  SUMMARY_ONLY: ["Somente resumo", "warning"],
  FILE_ONLY: ["Somente na pasta", "info"],
  FILE_DIVERGENT: ["Arquivo divergente", "error"],
  INVALID_FILE: ["Arquivo inválido", "error"],
  EVENT_ONLY: ["Somente evento", "warning"],
  INCONCLUSIVE: ["Inconclusivo", "warning"],
  NOT_FOUND: ["Não localizada", "warning"],
};

export function NfeKeyDiagnosticsPanel({ company }) {
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [report, setReport] = useState(null);
  const [error, setError] = useState("");

  async function verify(event) {
    event.preventDefault();
    if (busy) return;
    const keys = Array.from(new Set(text.split(/[\s;,]+/).map(v => v.trim()).filter(Boolean)));
    if (!keys.length || keys.length > 50 || keys.some(k => !/^\d{44}$/.test(k))) {
      setError("Informe de 1 a 50 chaves de acesso, cada uma com 44 números.");
      setReport(null);
      return;
    }
    setError("");
    setBusy(true);
    try {
      const response = await api.post("/api/nfe/diagnostics/keys", {
        companyId: company.companyId, keys,
      });
      setReport(response);
    } catch (err) {
      setError(err.message);
      setReport(null);
    } finally { setBusy(false); }
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <div className="rounded-lg bg-slate-100 p-2 text-slate-600"><Search size={18} /></div>
          <div>
            <h3 className="font-bold text-slate-900">Verificar XMLs não encontrados</h3>
            <p className="mt-1 text-sm text-slate-500">Confira se uma NF-e está no HANA e na pasta de XMLs, sem consultar a SEFAZ.</p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <form onSubmit={verify} className="space-y-3">
          <label className="block text-xs font-semibold text-slate-700" htmlFor="diagnostic-keys">
            Chaves de acesso (uma por linha)
          </label>
          <textarea id="diagnostic-keys" value={text} onChange={event => setText(event.target.value)}
            placeholder="Cole as chaves de 44 dígitos para verificar as notas."
            rows={4} className="w-full rounded-lg border border-slate-300 p-3 font-mono text-xs leading-relaxed focus:border-blue-500 focus:outline-none" />
          <Button type="submit" disabled={busy}>
            <FileCheck2 size={16} />{busy ? "Verificando registros..." : "Verificar XMLs"}
          </Button>
        </form>
        {error && <div className="mt-3"><Notice type="error">{error}</Notice></div>}
        {report && (
          <div aria-live="polite" className="mt-4 space-y-3">
            <p className="text-xs text-slate-600">
              {report.checked} chave(s) verificadas · {report.envelopesInspected} registro(s) examinados.
              {" "}{report.folderStatus}
            </p>
            {!report.scanComplete && <Notice type="warning">A consulta atingiu o limite de registros. Alguns resultados podem estar incompletos.</Notice>}
            <div className="overflow-x-auto rounded-lg border border-slate-200">
              <table className="min-w-full text-left text-xs">
                <thead className="bg-slate-50 text-slate-600">
                  <tr><th className="px-3 py-2">NF-e</th><th className="px-3 py-2">Resultado</th></tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {report.documents.map(item => {
                    const [label] = LABELS[item.status] || [item.status];
                    return (
                      <tr key={item.key}>
                        <td className="px-3 py-3 align-top">
                          <span className="font-semibold text-slate-900">Nº {Number(item.key.slice(25, 34))}</span>
                          <p className="mt-1 break-all font-mono text-[11px] text-slate-500">{item.key}</p>
                        </td>
                        <td className="px-3 py-3 align-top">
                          <p className="font-semibold text-slate-900">{label}</p>
                          <p className="mt-1 max-w-xs text-slate-600">{item.description}</p>
                          {item.schemas?.length > 0 &&
                            <p className="mt-1 text-slate-500">Registros: {item.schemas.join(", ")}</p>}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <p className="text-xs text-slate-500">A verificação não solicita XMLs à SEFAZ. Nota não localizada significa ausência nos registros examinados, não ausência na SEFAZ.</p>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
