import React, { useRef, useState } from "react";
import { Button, Caption1, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Spinner, Text, makeStyles, tokens } from "@fluentui/react-components";
import { CheckmarkRegular, DismissRegular, DocumentRegular, FolderOpenRegular, ShieldCheckmarkRegular } from "@fluentui/react-icons";
import { api, fileToBase64, formatDate } from "../api.js";
import Notice from "./Notice.jsx";
import PasswordInput from "./PasswordInput.jsx";

const useStyles = makeStyles({
  surface: { maxWidth: "560px" },
  actions: { "& button": { whiteSpace: "nowrap", minWidth: "max-content" } },
  content: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalL },
  intro: { color: tokens.colorNeutralForeground2 },
  picker: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalM, flexWrap: "wrap" },
  fileName: { display: "inline-flex", alignItems: "center", gap: tokens.spacingHorizontalXS, overflowWrap: "anywhere", minWidth: 0 },
  noFile: { color: tokens.colorNeutralForeground3 },
  hidden: { display: "none" },
  preview: {
    borderRadius: tokens.borderRadiusMedium, padding: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground3, border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  data: {
    display: "grid", gridTemplateColumns: "max-content 1fr", columnGap: tokens.spacingHorizontalXL, rowGap: tokens.spacingVerticalXS, margin: 0,
    "& dt": { color: tokens.colorNeutralForeground3 }, "& dd": { margin: 0, fontWeight: tokens.fontWeightSemibold, overflowWrap: "anywhere" },
    "@media (max-width: 480px)": { gridTemplateColumns: "1fr", "& dd": { marginBottom: tokens.spacingVerticalXS } },
  },
});

/** Envio do A1 em dois passos (validar → confirmar). mode "add" cadastra; mode "update" troca o A1 ou reativa. */
export default function CertificateDialog({ mode, company, onDone, onCancel }) {
  const styles = useStyles();
  const fileInput = useRef(null);
  const [file, setFile] = useState(null);
  const [password, setPassword] = useState("");
  const [preview, setPreview] = useState(null);
  const [replace, setReplace] = useState(false);
  const [busy, setBusy] = useState("");
  const [error, setError] = useState(null);

  const unreadable = company?.certificateStatus === "unreadable";
  const ready = file && password;
  const usable = preview && (mode === "add" ? !preview.existingCompanyId : preview.cnpj === company.cnpj);

  function reset() { setPreview(null); setError(null); }

  async function body() {
    return JSON.stringify({ pfxBase64: await fileToBase64(file), password, ...(unreadable && replace ? { replaceUnreadable: true } : {}) });
  }

  async function validate() {
    setBusy("validate"); setError(null); setPreview(null);
    try { setPreview(await api("/api/companies/certificates/inspect", { method: "POST", body: await body() })); }
    catch (e) { setError(e.message); }
    finally { setBusy(""); }
  }

  async function confirm() {
    setBusy("confirm"); setError(null);
    try {
      onDone(mode === "add"
        ? await api("/api/companies", { method: "POST", body: await body() })
        : await api(`/api/companies/${encodeURIComponent(company.companyId)}/certificate`, { method: "PUT", body: await body() }));
    } catch (e) { setError(e.message); setBusy(""); }
  }

  const title = mode === "add" ? "Adicionar empresa" : company.archived ? "Reativar empresa" : "Atualizar certificado A1";
  const confirmLabel = mode === "add" ? "Cadastrar empresa" : company.archived ? "Reativar empresa" : "Atualizar certificado";
  const intro = mode === "add"
    ? "Envie o certificado A1 da empresa. CNPJ e razão social são lidos do próprio certificado — nada é digitado."
    : `${company.legalName} · CNPJ ${company.cnpjFormatted}. O novo certificado precisa ser do mesmo CNPJ; cadastro e histórico são preservados.`;

  return <Dialog open modalType="modal" onOpenChange={(_, data) => { if (!data.open && !busy) onCancel(); }}>
    <DialogSurface className={styles.surface}>
      <form onSubmit={e => { e.preventDefault(); if (busy) return; if (usable) confirm(); else if (ready) validate(); }}>
        <DialogBody>
          <DialogTitle action={<Button appearance="subtle" aria-label="Fechar" icon={<DismissRegular/>} disabled={!!busy} onClick={onCancel}/>}>{title}</DialogTitle>
          <DialogContent className={styles.content}>
            <Text className={styles.intro}>{intro}</Text>
            <Field label="Arquivo do certificado A1" required hint="Formato .pfx ou .p12">
              <div className={styles.picker}>
                <input ref={fileInput} className={styles.hidden} type="file" accept=".pfx,.p12,application/x-pkcs12" aria-label="Arquivo do certificado A1"
                  onChange={e => { setFile(e.target.files[0] || null); reset(); }}/>
                <Button type="button" icon={<FolderOpenRegular/>} onClick={() => fileInput.current?.click()}>Escolher arquivo</Button>
                {file
                  ? <span className={styles.fileName}><DocumentRegular/> {file.name}</span>
                  : <Caption1 className={styles.noFile}>Nenhum arquivo selecionado</Caption1>}
              </div>
            </Field>
            <Field label="Senha do certificado" required>
              <PasswordInput value={password} onChange={value => { setPassword(value); reset(); }} placeholder="Senha do A1"/>
            </Field>
            {unreadable && <Checkbox checked={replace} onChange={(_, d) => setReplace(!!d.checked)}
              label="O certificado guardado não pode ser lido. Quero substituí-lo; o arquivo anterior será preservado como backup, não apagado."/>}
            {error && <Notice intent="error">{error}</Notice>}
            {preview && <>
              <Notice intent={usable ? "success" : "warning"}>
                {mode === "update" && !usable ? "Este certificado é de outro CNPJ e não pode ser usado nesta empresa." : preview.message}
              </Notice>
              <div className={styles.preview} data-testid="certificate-preview">
                <dl className={styles.data}>
                  <dt>CNPJ</dt><dd>{preview.cnpjFormatted}</dd>
                  <dt>Razão social</dt><dd>{preview.legalName}</dd>
                  <dt>UF</dt><dd>{preview.uf || "Não identificada no certificado"}</dd>
                  <dt>Emissor</dt><dd>{preview.issuer}</dd>
                  <dt>Validade</dt><dd>{formatDate(preview.notBeforeUtc)} a {formatDate(preview.notAfterUtc)} ({preview.daysRemaining} dias restantes)</dd>
                </dl>
              </div>
            </>}
          </DialogContent>
          <DialogActions className={styles.actions}>
            <Button type="button" appearance="secondary" disabled={!!busy} onClick={onCancel}>Cancelar</Button>
            {usable
              ? <Button type="submit" appearance="primary" icon={busy === "confirm" ? <Spinner size="tiny"/> : <CheckmarkRegular/>}
                  disabledFocusable={!!busy} disabled={unreadable && !replace}>{confirmLabel}</Button>
              : <Button type="submit" appearance="primary" icon={busy === "validate" ? <Spinner size="tiny"/> : <ShieldCheckmarkRegular/>}
                  disabledFocusable={!!busy} disabled={!ready}>Validar certificado</Button>}
          </DialogActions>
        </DialogBody>
      </form>
    </DialogSurface>
  </Dialog>;
}
