import { useState } from "react";
import { Unarchive, Search } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Notice } from "../common/Notice";
import { useNotices } from "../common/NoticesContext";
import { api } from "../../lib/api";
import { defaultRecoveryDate, maskDateInput, parseRecoveryDate } from "../../lib/capture-utils";

export function ZipRestorePanel({ company, preview, restoreResult, onPreview, onRestore, busy }) {
  const { notify } = useNotices();
  const [dateInput, setDateInput] = useState(defaultRecoveryDate);

  async function previewLocal() {
    try {
      const from = parseRecoveryDate(dateInput);
      const data = await api.get(`/api/recovery/nfe/preview/${company.companyId}?from=${encodeURIComponent(from)}`);
      onPreview(data);
    } catch (e) { notify.error(e.message); }
  }

  async function restore() {
    if (!preview?.scanComplete || preview.invalidFiles > 0) {
      notify.error("Antes de restaurar, resolva os arquivos inválidos ou a conferência incompleta."); return;
    }
    if (!window.confirm(
      "Restaurar TODOS os XMLs completos encontrados SOMENTE nos ZIPs internos para a pasta configurada?\n\n" +
      "Arquivos existentes não serão substituídos. Não haverá consulta à SEFAZ."
    )) return;
    try {
      const from = parseRecoveryDate(dateInput);
      const result = await api.post("/api/recovery/nfe/restore", { companyId: company.companyId, from });
      onRestore(result);
      await previewLocal();
    } catch (e) { notify.error(e.message); }
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <div className="p-2.5 bg-slate-100 rounded-lg text-slate-600"><Unarchive size={20} /></div>
          <div>
            <h3 className="text-base font-bold">Restauração de ZIPs locais</h3>
            <p className="text-sm text-slate-500 mt-0.5">Apenas lê envelopes brutos já arquivados localmente. Não consulta a SEFAZ.</p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <Notice type="info">
          A data selecionada filtra apenas o acervo local. Ela <strong>não</strong> é uma janela de
          consulta fiscal, e esta ação não altera o cursor do monitoramento.
        </Notice>
        <div className="mt-4 flex flex-wrap items-end gap-3">
          <div>
            <label htmlFor="zip-date" className="mb-1 block text-xs text-slate-600">Data inicial (DD/MM/AAAA)</label>
            <input id="zip-date" type="text" inputMode="numeric" maxLength={10}
              placeholder="DD/MM/AAAA" className="w-36 rounded-md border border-slate-300 p-2 text-sm"
              value={dateInput} onChange={(e) => setDateInput(maskDateInput(e.target.value))} />
          </div>
          <Button variant="outline" disabled={busy} onClick={previewLocal}>
            <Search size={16} />Conferir XMLs completos
          </Button>
          {preview && (
            <Button disabled={busy || !preview.scanComplete || preview.invalidFiles > 0} onClick={restore}>
              <Unarchive size={16} />Restaurar para pasta
            </Button>
          )}
        </div>
        {preview && (
          <div className="mt-4 rounded-lg bg-slate-50 p-3 text-sm">
            <div className="font-semibold">
              NF-e completas encontradas: {preview.candidates} ·
              Arquivos na pasta analisados: {preview.individualFiles} ·
              XMLs de ZIP analisados no período: {preview.archiveDocuments}
            </div>
            {!preview.scanComplete && (
              <p className="mt-2 text-xs text-amber-800">Conferência incompleta — não é possível restaurar até resolver.</p>
            )}
            {preview.invalidFiles > 0 && (
              <p className="mt-2 text-xs text-amber-800">{preview.invalidFiles} arquivo(s) inválido(s) impedem a restauração.</p>
            )}
            {preview.documents?.length > 0 ? (
              <div className="mt-2 max-h-48 overflow-auto">
                {preview.documents.map((doc) => (
                  <div key={doc.key} className="border-t border-slate-200 py-1 text-xs break-all">
                    NF-e · {doc.documentDate ? new Date(doc.documentDate).toLocaleDateString("pt-BR") : "sem data"} ·
                    {" "}Chave {doc.key} · {doc.source}
                    {!doc.xmlPresent ? " (somente no ZIP)" : ""}
                  </div>
                ))}
              </div>
            ) : (
              <p className="mt-2 text-xs text-slate-500">
                Nenhum procNFe identificado na pasta ou nos ZIPs internos. Isso não significa
                que nenhuma NF-e tenha sido emitida.
              </p>
            )}
          </div>
        )}
        {restoreResult && (
          <p className="mt-3 text-xs text-emerald-800">
            Restaurados: {restoreResult.restored ?? 0} · Já existentes: {restoreResult.alreadyPresent ?? 0}
          </p>
        )}
      </CardContent>
    </Card>
  );
}
