import React, { useEffect, useRef, useState } from "react";
import { Body1, Button, Caption1, Card, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Select, Spinner, Subtitle2, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Title2, Tooltip, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { ArrowDownloadRegular, ArrowSyncRegular, ChevronDownRegular, ChevronUpRegular, ClockRegular, DocumentCheckmarkRegular } from "@fluentui/react-icons";
import { api, countersFor, describeJob, describeManifest, DOCUMENT_KINDS, formatDate, formatDateTime, formatMoney, isJobActive, MANIFEST_LIMIT, selectableKeys } from "../api.js";
import Notice from "../components/Notice.jsx";

const SELECTED_KEY = "nexfiscal.empresaSelecionada";
function readSelected() { try { return localStorage.getItem(SELECTED_KEY) || ""; } catch { return ""; } }
function writeSelected(id) { try { localStorage.setItem(SELECTED_KEY, id); } catch { /* preferência opcional */ } }

const useStyles = makeStyles({
  top: { display: "flex", alignItems: "flex-start", gap: tokens.spacingHorizontalL },
  heading: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXS, flexGrow: 1 },
  lead: { color: tokens.colorNeutralForeground2, maxWidth: "660px" },
  card: { padding: tokens.spacingHorizontalXXL, gap: tokens.spacingVerticalL, "@media (max-width: 760px)": { padding: tokens.spacingHorizontalL } },
  fields: {
    display: "grid", gridTemplateColumns: "1fr 1fr", alignItems: "start", gap: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL}`,
    "@media (max-width: 760px)": { gridTemplateColumns: "1fr" },
  },
  actions: {
    display: "flex", alignItems: "center", justifyContent: "flex-end", gap: tokens.spacingHorizontalM, flexWrap: "wrap",
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`, paddingTop: tokens.spacingVerticalL,
  },
  hint: { color: tokens.colorNeutralForeground3, marginRight: "auto", display: "inline-flex", alignItems: "center", gap: tokens.spacingHorizontalXS },
  counters: {
    display: "grid", gridTemplateColumns: "repeat(3, 1fr)", gap: tokens.spacingHorizontalM,
    "@media (max-width: 760px)": { gridTemplateColumns: "repeat(2, 1fr)" },
  },
  counter: {
    display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXXS, padding: tokens.spacingHorizontalL,
    borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  value: { fontSize: tokens.fontSizeHero700, lineHeight: tokens.lineHeightHero700, fontWeight: tokens.fontWeightSemibold },
  ok: { color: tokens.colorPaletteGreenForeground1 },
  warn: { color: tokens.colorPaletteDarkOrangeForeground1 },
  error: { color: tokens.colorPaletteRedForeground1 },
  label: { color: tokens.colorNeutralForeground2 },
  meta: { color: tokens.colorNeutralForeground3 },
  loading: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS },
  tableWrap: { overflowX: "auto" },
  key: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, overflowWrap: "anywhere" },
  pendingActions: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalM, flexWrap: "wrap" },
  check: { width: "44px" },
  neutral: { color: tokens.colorNeutralForeground3 },
  dialog: { maxWidth: "560px" },
  dialogText: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalM },
});

export default function CapturePage({ hana, goToHana, goToCompanies }) {
  const styles = useStyles();
  const [companies, setCompanies] = useState(null);
  const [companyId, setCompanyId] = useState("");
  const [kind, setKind] = useState("nfe");
  const doc = DOCUMENT_KINDS[kind];
  const [view, setView] = useState(null);
  const [folder, setFolder] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [pending, setPending] = useState(null); // null = lista fechada
  const [marked, setMarked] = useState([]); // notas marcadas para a ciência
  const [confirming, setConfirming] = useState(false);
  const [confirmCount, setConfirmCount] = useState(0); // fixo enquanto o diálogo fecha
  const [sending, setSending] = useState(false);
  const [manifestResult, setManifestResult] = useState(null);
  const folderTouched = useRef(false);

  const hanaReady = hana?.state === "ready";

  useEffect(() => {
    if (!hanaReady) return;
    api("/api/companies").then(list => {
      const active = list.filter(c => !c.archived);
      setCompanies(active);
      const remembered = readSelected();
      setCompanyId(active.some(c => c.companyId === remembered) ? remembered : active[0]?.companyId || "");
    }).catch(e => { setCompanies([]); setError(e.message); });
  }, [hanaReady]);

  // background = atualização automática: uma falha passageira não apaga o que está na tela nem interrompe o acompanhamento.
  async function load(id, background) {
    try {
      const next = await api(`/api/companies/${encodeURIComponent(id)}/${kind}/capture`);
      setView(next);
      if (!folderTouched.current) setFolder(next.destinationDirectory || "");
      if (!background) setError(null);
      return next;
    } catch (e) {
      if (!background) { setView(null); setError(e.message); }
      return null;
    }
  }

  useEffect(() => {
    setView(null); setPending(null); setMarked([]); setManifestResult(null); folderTouched.current = false; setFolder("");
    if (companyId) load(companyId);
  }, [companyId, kind]);

  // Enquanto houver pedido ativo, a tela acompanha sozinha: rápido durante a consulta, devagar na espera da SEFAZ.
  const active = isJobActive(view?.job);
  const waiting = view?.job?.state === "waiting_sefaz";
  useEffect(() => {
    if (!active || !companyId) return;
    const timer = setInterval(() => load(companyId, true), waiting ? 30000 : 2500);
    return () => clearInterval(timer);
  }, [active, waiting, companyId, kind]);

  async function capture() {
    setBusy(true); setError(null);
    try {
      await api(`/api/companies/${encodeURIComponent(companyId)}/${kind}/captures`, { method: "POST", body: JSON.stringify({ destinationDirectory: folder }) });
      folderTouched.current = false;
      await load(companyId);
    } catch (e) { setError(e.message); }
    finally { setBusy(false); }
  }

  async function loadPending() {
    const notes = await api(`/api/companies/${encodeURIComponent(companyId)}/nfe/pending`);
    setPending(notes);
    const allowed = new Set(selectableKeys(notes));
    setMarked(keys => keys.filter(key => allowed.has(key)));
  }

  async function togglePending() {
    if (pending) { setPending(null); setMarked([]); return; }
    try { await loadPending(); }
    catch (e) { setError(e.message); }
  }

  function toggleMark(key, checked) {
    setMarked(keys => checked ? (keys.includes(key) || keys.length >= MANIFEST_LIMIT ? keys : [...keys, key]) : keys.filter(k => k !== key));
  }

  // Só acontece depois de o operador marcar as notas e confirmar no diálogo.
  async function sendManifest() {
    setSending(true); setError(null); setManifestResult(null);
    let outcome;
    try {
      const result = await api(`/api/companies/${encodeURIComponent(companyId)}/nfe/manifest`, { method: "POST", body: JSON.stringify({ accessKeys: marked }) });
      outcome = { intent: result.rejected > 0 || result.unanswered > 0 ? "warning" : "success", message: result.message };
      setMarked([]);
    } catch (e) { outcome = { intent: "error", message: e.message }; }
    // A lista é relida antes de mostrar o resultado, para a situação de cada nota já aparecer atualizada.
    try { await loadPending(); } catch { /* a lista anterior continua na tela */ }
    setManifestResult(outcome); setSending(false); setConfirming(false);
  }

  const heading = <div className={styles.heading}>
    <Title2 as="h1">Captura de {doc.label}</Title2>
    <Body1 className={styles.lead}>{doc.lead}</Body1>
  </div>;

  if (!hanaReady)
    return <>
      {heading}
      {hana === undefined
        ? <div className={styles.loading} role="status"><Spinner size="tiny"/> <Text>Verificando a conexão SAP HANA...</Text></div>
        : <Notice intent="warning" title="Conexão SAP HANA necessária" actions={<Button appearance="primary" onClick={goToHana}>Abrir Conexão SAP HANA</Button>}>
            A captura grava o controle no SAP HANA. {hana?.message || "Configure a conexão primeiro."}
          </Notice>}
    </>;

  if (companies === null)
    return <>{heading}<div className={styles.loading} role="status"><Spinner size="tiny"/> <Text>Carregando empresas...</Text></div></>;

  if (companies.length === 0)
    return <>
      {heading}
      {error && <Notice intent="error">{error}</Notice>}
      <Notice intent="warning" title="Nenhuma empresa cadastrada" actions={<Button appearance="primary" onClick={goToCompanies}>Abrir Empresas</Button>}>
        Cadastre a empresa com o certificado A1 para poder capturar.
      </Notice>
    </>;

  const job = view?.job;
  const state = describeJob(job, doc.source);
  const canStart = view?.canCapture && folder.trim() && !active && !busy;

  return <>
    <div className={styles.top}>
      {heading}
      <Tooltip content="Atualizar" relationship="label">
        <Button appearance="subtle" icon={<ArrowSyncRegular/>} disabled={!companyId} onClick={() => load(companyId)}/>
      </Tooltip>
    </div>

    <TabList selectedValue={kind} onTabSelect={(_, data) => setKind(data.value)} aria-label="Tipo de documento">
      <Tab value="nfe" disabled={busy}>NF-e</Tab>
      <Tab value="nfse" disabled={busy}>NFS-e</Tab>
    </TabList>

    <Card className={styles.card}>
      <div className={styles.fields}>
        <Field label="Empresa" required>
          <Select value={companyId} onChange={(_, data) => { setCompanyId(data.value); writeSelected(data.value); }} disabled={active || busy}>
            {companies.map(c => <option key={c.companyId} value={c.companyId}>{c.legalName} — {c.cnpjFormatted}</option>)}
          </Select>
        </Field>
        <Field label="Pasta de destino" required hint={`Pasta neste computador. Cada ${doc.label} é gravada como <chave de acesso>.xml`}>
          <Input value={folder} placeholder="C:\NexFiscal\XML" spellCheck={false} autoComplete="off" disabled={active || busy}
            onChange={(_, data) => { folderTouched.current = true; setFolder(data.value); }}/>
        </Field>
      </div>
      <div className={styles.actions}>
        <Caption1 className={styles.hint}>
          {view?.nextAllowedAtUtc
            ? <><ClockRegular/> Próxima consulta {doc.sourceTo} permitida em {formatDateTime(view.nextAllowedAtUtc)}</>
            : view ? "A consulta pode ser feita agora." : ""}
        </Caption1>
        <Button appearance="primary" size="large" icon={busy ? <Spinner size="tiny"/> : <ArrowDownloadRegular/>} disabled={!canStart} onClick={capture}>
          Capturar todos os XMLs disponíveis
        </Button>
      </div>
    </Card>

    {view && !view.canCapture && <Notice intent="error" title="Captura bloqueada" actions={<Button onClick={goToCompanies}>Abrir Empresas</Button>}>{view.blockedReason}</Notice>}
    {error && <Notice intent="error" onDismiss={() => setError(null)}>{error}</Notice>}

    {state && <>
      <Notice intent={state.intent} title={state.busy ? <><Spinner size="extra-tiny" style={{ display: "inline-flex", marginRight: 8 }}/>{state.title}</> : state.title}>
        {job.message}
      </Notice>
      <section className={styles.counters} aria-label="Resultado da captura">
        {countersFor(kind, job).map(counter => <div key={counter.key} className={styles.counter} data-counter={counter.key}>
          <span className={mergeClasses(styles.value, styles[counter.tone])}>{counter.value}</span>
          <Caption1 className={styles.label}>{counter.label}</Caption1>
        </div>)}
      </section>
      <Caption1 className={styles.meta}>
        Pedido feito em {formatDateTime(job.requestedAtUtc)}
        {job.finishedAtUtc ? ` · concluído em ${formatDateTime(job.finishedAtUtc)}` : ""} · pasta {job.destinationDirectory}
      </Caption1>
    </>}

    {doc.pending && view?.pendingSummaries > 0 && <Card className={styles.card}>
      <Subtitle2 as="h2">{view.pendingSummaries} nota(s) aguardando manifestação do destinatário</Subtitle2>
      <Body1 className={styles.lead}>
        Para estas notas a SEFAZ entregou apenas o resumo. O XML completo só é liberado depois que a empresa registra a
        Ciência da Operação. Marque as notas e registre a ciência aqui; em seguida o XML chega na próxima captura.
        O NexFiscal nunca envia a ciência sozinho.
      </Body1>
      <div className={styles.pendingActions}>
        <Button icon={pending ? <ChevronUpRegular/> : <ChevronDownRegular/>} onClick={togglePending}>{pending ? "Ocultar notas" : "Ver notas pendentes"}</Button>
        {pending && <Button appearance="primary" icon={<DocumentCheckmarkRegular/>} disabled={marked.length === 0 || sending || !view.canCapture}
          onClick={() => { setConfirmCount(marked.length); setConfirming(true); }}>
          Registrar ciência da operação{marked.length > 0 ? ` (${marked.length})` : ""}
        </Button>}
      </div>
      {manifestResult && <Notice intent={manifestResult.intent} title="Ciência da Operação" onDismiss={() => setManifestResult(null)}>{manifestResult.message}</Notice>}
      {pending && (() => {
        const selectable = selectableKeys(pending);
        const all = selectable.length > 0 && selectable.every(key => marked.includes(key));
        return <div className={styles.tableWrap}>
          <Table size="small" aria-label="Notas aguardando manifestação">
            <TableHeader><TableRow>
              <TableHeaderCell className={styles.check}>
                <Checkbox aria-label="Marcar todas" disabled={selectable.length === 0 || sending}
                  checked={all ? true : marked.length > 0 ? "mixed" : false}
                  onChange={(_, data) => setMarked(data.checked === true ? selectable : [])}/>
              </TableHeaderCell>
              <TableHeaderCell>Emitente</TableHeaderCell><TableHeaderCell>Emissão</TableHeaderCell>
              <TableHeaderCell>Valor</TableHeaderCell><TableHeaderCell>Ciência</TableHeaderCell><TableHeaderCell>Chave de acesso</TableHeaderCell>
            </TableRow></TableHeader>
            <TableBody>
              {pending.map(note => {
                const situation = describeManifest(note);
                return <TableRow key={note.accessKey}>
                  <TableCell className={styles.check}>
                    <Checkbox aria-label={`Marcar nota de ${note.issuerName || note.accessKey}`} disabled={!note.canManifest || sending}
                      checked={marked.includes(note.accessKey)} onChange={(_, data) => toggleMark(note.accessKey, data.checked === true)}/>
                  </TableCell>
                  <TableCell>{note.issuerName || "—"}</TableCell>
                  <TableCell>{formatDate(note.issuedAtUtc)}</TableCell>
                  <TableCell>{formatMoney(note.totalValue)}</TableCell>
                  <TableCell><span className={styles[situation.tone]} data-manifest={note.manifestState || "none"}>{situation.text}</span></TableCell>
                  <TableCell><span className={styles.key}>{note.accessKey}</span></TableCell>
                </TableRow>;
              })}
            </TableBody>
          </Table>
          {pending.length >= 200 && <Caption1 className={styles.meta}>Exibindo as 200 mais recentes.</Caption1>}
          {selectable.length >= MANIFEST_LIMIT && <Caption1 className={styles.meta}>Cada envio registra até {MANIFEST_LIMIT} notas.</Caption1>}
        </div>;
      })()}
    </Card>}

    <Dialog open={confirming} onOpenChange={(_, data) => { if (!sending) setConfirming(data.open); }}>
      <DialogSurface className={styles.dialog}>
        <DialogBody>
          <DialogTitle>Registrar ciência de {confirmCount} nota(s)?</DialogTitle>
          <DialogContent className={styles.dialogText}>
            <Text>O NexFiscal vai enviar à SEFAZ, assinado com o certificado A1 da empresa, o evento <b>Ciência da Operação</b> para as notas marcadas.</Text>
            <Text>A ciência informa que a empresa sabe da existência da nota. Ela não confirma nem recusa a operação, e <b>não pode ser desfeita</b>.
              A manifestação conclusiva (confirmação, desconhecimento ou operação não realizada) continua sendo feita fora do NexFiscal, no prazo legal.</Text>
            <Text>Depois do registro, a SEFAZ libera o XML completo, que é baixado na próxima captura.</Text>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" disabled={sending} onClick={() => setConfirming(false)}>Cancelar</Button>
            <Button appearance="primary" disabledFocusable={sending} icon={sending ? <Spinner size="tiny"/> : undefined} onClick={() => { if (!sending) sendManifest(); }}>
              {sending ? "Enviando..." : "Registrar ciência"}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  </>;
}
