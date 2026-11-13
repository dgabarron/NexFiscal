import { useState } from "react";
import { Unarchive, Search } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Notice } from "../common/Notice";
import { useNotices } from "../common/NoticesContext";
import { api } from "../../lib/api";
import { defaultRecoveryDate, maskDateInput, parseRecoveryDate } from "../../lib/capture-utils";

/** Estado restrito ao CNPJ (key no pai); mudar data invalida a prévia. */
export function ZipRestorePanel({ company }) {
  const { notify } = useNotices();
  const [dateInput, setDateInput] = useState(defaultRecoveryDate);
  const [preview, setPreview] = useState(null);
  const [previewFrom, setPreviewFrom] = useState(null);
  const [restoreResult, setRestoreResult] = useState(null);
  const [busy, setBusy] = useState(false);

  function changeDate(value) {
    setDateInput(maskDateInput(value));
    setPreview(null);
    setPreviewFrom(null);
    setRestoreResult(null);
  }

  async function previewLocal() {
    if (busy) return;
    let from;
    try { from = parseRecoveryDate(dateInput); }
    catch (e) { notify.error(e.message); return; }
    setBusy(true);
    setPreview(null);
    setPreviewFrom(null);
    setRestoreResult(null);
    try {
      const result = await api.get(`/api/recovery/nfe/preview/${company.companyId}?from=${encodeURIComponent(from)}`);
      setPreview(result);
      setPreviewFrom(from);
    } catch (e) { notify.error(e.message); }
    finally { setBusy(false); }
  }

  async function restore() {
    if (busy) return;
    let from;
    try { from = parseRecoveryDate(dateInput); }
    catch (e) { notify.error(e.message); return; }
    if (from !== previewFrom || !preview?.scanComplete || preview.invalidFiles > 0) {
      notify.error("Confira novamente os XMLs deste período antes da restauração."); return;
    }
    if (!window.confirm(
      "Restaurar somente XMLs completos encontrados nos ZIPs internos?\n\n" +
      "Nenhum arquivo existente será substituído. Não haverá consulta à SEFAZ."
    )) return;
    setBusy(true);
    try {
      const result = await api.post("/api/recovery/nfe/restore", {
        companyId: company.companyId, from,
      });
      setRestoreResult(result);
      const next = await api.get(`/api/recovery/nfe/preview/${company.companyId}?from=${encodeURIComponent(from)}`);
      setPreview(next);
      setPreviewFrom(from);
      notify.success("Restauração local concluída.");
    } catch (e) {
      setPreview(null);
      setPreviewFrom(null);
      notify.error(e.message);
    } finally { setBusy(false); }
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <div className="rounded-lg bg-slate-100 p-2.5 text-slate-600"><Unarchive size={20} /></div>
          <div>
            <h3 className="text-base font-bold">Restauração de ZIPs locais</h3>
            <p className="mt-1 text-sm text-slate-500">
              Recupera arquivos previamente arquivados. Não chama a SEFAZ nem altera NSU.
            </p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <Notice type="info">
          A data selecionada filtra exclusivamente o acervo local, dentro do limite atual de
          90 dias. Não é um filtro de consulta fiscal.
        </Notice>
        <div className="mt-4 flex flex-wrap items-end gap-3">
          <div>
            <label htmlFor="zip-date" className="mb-1 block text-xs text-slate-600">
              Data inicial (DD/MM/AAAA)
            </label>
            <input id="zip-date" type="text" inputMode="numeric" maxLength={10}
              placeholder="DD/MM/AAAA" className="w-36 rounded-md border border-slate-300 p-2 text-sm"
              value={dateInput} disabled={busy}
              onChange={(e) => changeDate(e.target.value)} />
          </div>
          <Button variant="outline" disabled={busy} onClick={previewLocal}>
            <Search size={16} />{busy ? "Processando..." : "Conferir XMLs completos"}
          </Button>
          {preview && (
            <Button disabled={busy || previewFrom !== parseRecoveryDate(dateInput) ||
              !preview.scanComplete || preview.invalidFiles > 0} onClick={restore}>
              <Unarchive size={16} />Restaurar para pasta
            </Button>
          )}
        </div>
        {preview && (
          <div className="mt-4 rounded-lg bg-slate-50 p-3 text-sm">
            <p className="font-semibold">
              NF-e completas encontradas: {preview.candidates ?? 0} ·
              Arquivos individuais: {preview.individualFiles ?? 0} ·
              Documentos nos ZIPs: {preview.archiveDocuments ?? 0}
            </p>
            {!preview.scanComplete && <p className="mt-2 text-amber-800">Conferência incompleta; não é possível restaurar.</p>}
            {!!preview.invalidFiles && <p className="mt-2 text-amber-800">
              {preview.invalidFiles} arquivo(s) inválido(s) impedem a restauração.
            </p>}
            {preview.documents?.length > 0 ? (
              <div className="mt-2 max-h-48 overflow-auto">
                {preview.documents.map((doc) => (
                  <p key={doc.key} className="border-t border-slate-200 py-1 text-xs break-all">
                    NF-e · {doc.documentDate ? new Date(doc.documentDate).toLocaleDateString("pt-BR") : "sem data"} ·
                    Chave {doc.key} · {doc.source}
                    {!doc.xmlPresent && " (somente no ZIP)"}
                  </p>
                ))}
              </div>
            ) : <p className="mt-2 text-xs text-slate-500">
              Nenhum procNFe identificado no acervo; isso não comprova ausência de emissão.
            </p>}
          </div>
        )}
        {restoreResult && <p className="mt-3 text-sm text-emerald-800">
          Restaurados: {restoreResult.restored ?? 0} ·
          Já existentes: {restoreResult.already ?? 0}
        </p>}
      </CardContent>
    </Card>
  );
}
