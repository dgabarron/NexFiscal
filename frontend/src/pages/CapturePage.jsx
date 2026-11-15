import React, { useEffect, useRef, useState } from "react";
import { Body1, Button, Caption1, Card, Field, Input, Select, Spinner, Subtitle2, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Title2, Tooltip, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { ArrowDownloadRegular, ArrowSyncRegular, ChevronDownRegular, ChevronUpRegular, ClockRegular } from "@fluentui/react-icons";
import { api, describeJob, formatDate, formatDateTime, formatMoney, isJobActive, jobCounters } from "../api.js";
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
});

export default function CapturePage({ hana, goToHana, goToCompanies }) {
  const styles = useStyles();
  const [companies, setCompanies] = useState(null);
  const [companyId, setCompanyId] = useState("");
  const [view, setView] = useState(null);
  const [folder, setFolder] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [pending, setPending] = useState(null); // null = lista fechada
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
      const next = await api(`/api/companies/${encodeURIComponent(id)}/nfe/capture`);
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
    setView(null); setPending(null); folderTouched.current = false; setFolder("");
    if (companyId) load(companyId);
  }, [companyId]);

  // Enquanto houver pedido ativo, a tela acompanha sozinha: rápido durante a consulta, devagar na espera da SEFAZ.
  const active = isJobActive(view?.job);
  const waiting = view?.job?.state === "waiting_sefaz";
  useEffect(() => {
    if (!active || !companyId) return;
    const timer = setInterval(() => load(companyId, true), waiting ? 30000 : 2500);
    return () => clearInterval(timer);
  }, [active, waiting, companyId]);

  async function capture() {
    setBusy(true); setError(null);
    try {
      await api(`/api/companies/${encodeURIComponent(companyId)}/nfe/captures`, { method: "POST", body: JSON.stringify({ destinationDirectory: folder }) });
      folderTouched.current = false;
      await load(companyId);
    } catch (e) { setError(e.message); }
    finally { setBusy(false); }
  }

  async function togglePending() {
    if (pending) { setPending(null); return; }
    try { setPending(await api(`/api/companies/${encodeURIComponent(companyId)}/nfe/pending`)); }
    catch (e) { setError(e.message); }
  }

  const heading = <div className={styles.heading}>
    <Title2 as="h1">Captura de NF-e</Title2>
    <Body1 className={styles.lead}>Baixa da SEFAZ todos os XMLs completos de NF-e disponíveis para a empresa e que ainda não estão na pasta, um arquivo por nota. Nenhuma chave ou número precisa ser digitado.</Body1>
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
  const state = describeJob(job);
  const canStart = view?.canCapture && folder.trim() && !active && !busy;

  return <>
    <div className={styles.top}>
      {heading}
      <Tooltip content="Atualizar" relationship="label">
        <Button appearance="subtle" icon={<ArrowSyncRegular/>} disabled={!companyId} onClick={() => load(companyId)}/>
      </Tooltip>
    </div>

    <Card className={styles.card}>
      <div className={styles.fields}>
        <Field label="Empresa" required>
          <Select value={companyId} onChange={(_, data) => { setCompanyId(data.value); writeSelected(data.value); }} disabled={active || busy}>
            {companies.map(c => <option key={c.companyId} value={c.companyId}>{c.legalName} — {c.cnpjFormatted}</option>)}
          </Select>
        </Field>
        <Field label="Pasta de destino" required hint="Pasta neste computador. Cada NF-e é gravada como <chave de acesso>.xml">
          <Input value={folder} placeholder="C:\NexFiscal\XML" spellCheck={false} autoComplete="off" disabled={active || busy}
            onChange={(_, data) => { folderTouched.current = true; setFolder(data.value); }}/>
        </Field>
      </div>
      <div className={styles.actions}>
        <Caption1 className={styles.hint}>
          {view?.nextAllowedAtUtc
            ? <><ClockRegular/> Próxima consulta à SEFAZ permitida em {formatDateTime(view.nextAllowedAtUtc)}</>
            : view ? "A SEFAZ pode ser consultada agora." : ""}
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
        {jobCounters(job).map(counter => <div key={counter.key} className={styles.counter} data-counter={counter.key}>
          <span className={mergeClasses(styles.value, styles[counter.tone])}>{counter.value}</span>
          <Caption1 className={styles.label}>{counter.label}</Caption1>
        </div>)}
      </section>
      <Caption1 className={styles.meta}>
        Pedido feito em {formatDateTime(job.requestedAtUtc)}
        {job.finishedAtUtc ? ` · concluído em ${formatDateTime(job.finishedAtUtc)}` : ""} · pasta {job.destinationDirectory}
      </Caption1>
    </>}

    {view?.pendingSummaries > 0 && <Card className={styles.card}>
      <Subtitle2 as="h2">{view.pendingSummaries} nota(s) aguardando manifestação do destinatário</Subtitle2>
      <Body1 className={styles.lead}>
        Para estas notas a SEFAZ entregou apenas o resumo. O XML completo só é liberado ao destinatário depois da manifestação
        (Ciência da Operação ou Confirmação), feita no portal da SEFAZ ou no seu sistema fiscal. O NexFiscal não envia manifestação;
        depois dela, o XML chega sozinho na próxima captura.
      </Body1>
      <div>
        <Button icon={pending ? <ChevronUpRegular/> : <ChevronDownRegular/>} onClick={togglePending}>{pending ? "Ocultar notas" : "Ver notas pendentes"}</Button>
      </div>
      {pending && <div className={styles.tableWrap}>
        <Table size="small" aria-label="Notas aguardando manifestação">
          <TableHeader><TableRow>
            <TableHeaderCell>Emitente</TableHeaderCell><TableHeaderCell>Emissão</TableHeaderCell>
            <TableHeaderCell>Valor</TableHeaderCell><TableHeaderCell>Chave de acesso</TableHeaderCell>
          </TableRow></TableHeader>
          <TableBody>
            {pending.map(note => <TableRow key={note.accessKey}>
              <TableCell>{note.issuerName || "—"}</TableCell>
              <TableCell>{formatDate(note.issuedAtUtc)}</TableCell>
              <TableCell>{formatMoney(note.totalValue)}</TableCell>
              <TableCell><span className={styles.key}>{note.accessKey}</span></TableCell>
            </TableRow>)}
          </TableBody>
        </Table>
        {pending.length >= 200 && <Caption1 className={styles.meta}>Exibindo as 200 mais recentes.</Caption1>}
      </div>}
    </Card>}
  </>;
}
