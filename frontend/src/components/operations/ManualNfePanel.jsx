import { useState } from "react";
import { FileDown } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Notice } from "../common/Notice";
import { api } from "../../lib/api";
import { useNotices } from "../common/NoticesContext";

/** Preserva a consulta manual de um lote NF-e existente antes do redesign. */
export function ManualNfePanel({ company, disabled, onCompleted }) {
  const { notify } = useNotices();
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState(null);

  async function download() {
    if (busy || disabled) return;
    if (!window.confirm(
      "Consultar uma vez a distribuição de NF-e para o CNPJ selecionado? " +
      "A operação usa o checkpoint atual e respeita a janela fiscal da SEFAZ."
    )) return;
    setBusy(true);
    setResult(null);
    try {
      const response = await api.post("/api/capture/download", { companyId: company.companyId });
      setResult(response);
      if (response.saveSucceeded === false || response.hanaSaved === false) {
        notify.error(response.fileSaveError || response.persistenceWarning ||
          "O resultado da consulta exige reconciliação antes de nova tentativa.");
      } else if (response.status === 656) {
        notify.error("SEFAZ 656: consumo indevido. Respeite o intervalo de segurança.");
      } else if (response.status === 137) {
        notify.success("SEFAZ 137: nenhum novo documento disponível.");
      } else if (response.status === 138 && !response.filesSaved && !response.filesAlreadyPresent) {
        notify.success("Documentos recebidos, mas nenhum procNFe completo nesta consulta.");
      } else {
        notify.success(`Consulta concluída. ${response.filesSaved ?? 0} XML(s) completos novos; ${response.filesAlreadyPresent ?? 0} já existentes.`);
      }
      await onCompleted?.();
    } catch (e) { notify.error(e.message); }
    finally { setBusy(false); }
  }

  return (
    <Card>
      <CardHeader>
        <h3 className="text-base font-bold">Consulta manual de um lote</h3>
        <p className="mt-1 text-sm text-slate-500">
          Executa uma consulta `distNSU` e apresenta o resultado. Não substitui o Worker
          nem a sincronização completa dos lotes disponíveis.
        </p>
      </CardHeader>
      <CardContent>
        <Button variant="outline" onClick={download} disabled={disabled || busy}>
          <FileDown size={16} />{busy ? "Consultando..." : "Consultar um lote NF-e"}
        </Button>
        {result && (
          <div className="mt-4">
            <Notice type={result.saveSucceeded === false || result.hanaSaved === false ? "error"
              : result.status === 137 || result.status === 656 ? "warning" : "info"}>
              SEFAZ {result.status} · {result.reason} ·
              XMLs novos: {result.filesSaved ?? 0} ·
              Já existentes: {result.filesAlreadyPresent ?? 0} ·
              Resumos/eventos: {result.skippedNonProcNFe ?? 0}
              {result.persistenceWarning && <p className="mt-2">{result.persistenceWarning}</p>}
              {result.archiveUrl && (
                <p className="mt-2">
                  <a className="underline" href={result.archiveUrl}>Baixar ZIP do lote recebido</a>
                </p>
              )}
            </Notice>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
