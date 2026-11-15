import React, { useEffect, useRef, useState } from "react";
import { Body1, Button, Caption1, Card, Field, Input, Select, Spinner, Subtitle2, Switch, Text, Title2, Tooltip, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { ArrowSyncRegular, InfoRegular, SaveRegular } from "@fluentui/react-icons";
import { api, describeJob, describeMonitor, formatDateTime, isIntervalValid, jobCounters } from "../api.js";
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
    display: "grid", gridTemplateColumns: "1fr 2fr", alignItems: "start", gap: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL}`,
    "@media (max-width: 760px)": { gridTemplateColumns: "1fr" },
  },
  actions: {
    display: "flex", alignItems: "center", justifyContent: "flex-end", gap: tokens.spacingHorizontalM, flexWrap: "wrap",
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`, paddingTop: tokens.spacingVerticalL,
  },
  hint: { color: tokens.colorNeutralForeground3, marginRight: "auto" },
  counters: {
    display: "grid", gridTemplateColumns: "repeat(3, 1fr)", gap: tokens.spacingHorizontalM,
    "@media (max-width: 760px)": { gridTemplateColumns: "repeat(2, 1fr)" },
  },
  counter: {
    display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXXS, padding: tokens.spacingHorizontalL,
    borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  value: { fontSize: tokens.fontSizeHero700, lineHeight: tokens.lineHeightHero700, fontWeight: tokens.fontWeightSemibold },
  ok: { color: tokens.colorPaletteGreenForeground1 },
  warn: { color: tokens.colorPaletteDarkOrangeForeground1 },
  error: { color: tokens.colorPaletteRedForeground1 },
  label: { color: tokens.colorNeutralForeground2 },
  meta: { color: tokens.colorNeutralForeground3 },
  note: { display: "flex", alignItems: "flex-start", gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground3 },
  loading: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS },
});

export default function MonitorPage({ hana, goToHana, goToCompanies }) {
  const styles = useStyles();
  const [companies, setCompanies] = useState(null);
  const [companyId, setCompanyId] = useState("");
  const [view, setView] = useState(null);
  const [form, setForm] = useState({ enabled: false, interval: "60", folder: "" });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [saved, setSaved] = useState(null);
  const dirty = useRef(false);

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

  function show(next) {
    setView(next);
    // O que o operador está editando não é sobrescrito pela atualização automática.
    if (!dirty.current)
      setForm({ enabled: next.enabled, interval: String(next.intervalMinutes), folder: next.destinationDirectory || "" });
  }

  async function load(id, background) {
    try { show(await api(`/api/companies/${encodeURIComponent(id)}/nfe/monitor`)); if (!background) setError(null); }
    catch (e) { if (!background) { setView(null); setError(e.message); } }
  }

  useEffect(() => {
    setView(null); setSaved(null); dirty.current = false;
    if (companyId) load(companyId);
  }, [companyId]);

  // Com o monitoramento ligado, a tela se atualiza sozinha para mostrar a última execução.
  useEffect(() => {
    if (!view?.enabled || !companyId) return;
    const timer = setInterval(() => load(companyId, true), 20000);
    return () => clearInterval(timer);
  }, [view?.enabled, companyId]);

  function change(patch) { dirty.current = true; setSaved(null); setForm(current => ({ ...current, ...patch })); }

  async function save() {
    setBusy(true); setError(null); setSaved(null);
    try {
      const next = await api(`/api/companies/${encodeURIComponent(companyId)}/nfe/monitor`, {
        method: "PUT", body: JSON.stringify({ enabled: form.enabled, intervalMinutes: Number(form.interval), destinationDirectory: form.folder })
      });
      dirty.current = false;
      show(next);
      setSaved(next.enabled ? "Monitoramento automático ativado e configuração salva." : "Monitoramento automático desligado. Histórico e arquivos foram mantidos.");
    } catch (e) { setError(e.message); }
    finally { setBusy(false); }
  }

  const heading = <div className={styles.heading}>
    <Title2 as="h1">Monitoramento automático</Title2>
    <Body1 className={styles.lead}>Captura as NF-e novas sozinho, na periodicidade escolhida, usando o mesmo processo da captura manual. Só funciona para as empresas em que você ligar.</Body1>
  </div>;

  if (!hanaReady)
    return <>
      {heading}
      {hana === undefined
        ? <div className={styles.loading} role="status"><Spinner size="tiny"/> <Text>Verificando a conexão SAP HANA...</Text></div>
        : <Notice intent="warning" title="Conexão SAP HANA necessária" actions={<Button appearance="primary" onClick={goToHana}>Abrir Conexão SAP HANA</Button>}>
            A configuração do monitoramento é gravada no SAP HANA. {hana?.message || "Configure a conexão primeiro."}
          </Notice>}
    </>;

  if (companies === null)
    return <>{heading}<div className={styles.loading} role="status"><Spinner size="tiny"/> <Text>Carregando empresas...</Text></div></>;

  if (companies.length === 0)
    return <>
      {heading}
      {error && <Notice intent="error">{error}</Notice>}
      <Notice intent="warning" title="Nenhuma empresa cadastrada" actions={<Button appearance="primary" onClick={goToCompanies}>Abrir Empresas</Button>}>
        Cadastre a empresa com o certificado A1 para poder monitorar.
      </Notice>
    </>;

  const state = describeMonitor(view);
  const intervalValid = isIntervalValid(form.interval);
  const canSave = view && !busy && intervalValid && (!form.enabled || form.folder.trim());
  const job = view?.lastJob;
  const jobState = describeJob(job);

  return <>
    <div className={styles.top}>
      {heading}
      <Tooltip content="Atualizar" relationship="label">
        <Button appearance="subtle" icon={<ArrowSyncRegular/>} disabled={!companyId} onClick={() => load(companyId)}/>
      </Tooltip>
    </div>

    {state && <Notice intent={state.intent} title={state.title}
      actions={view.enabled && !view.canCapture && <Button onClick={goToCompanies}>Abrir Empresas</Button>}>
      {view.enabled && !view.canCapture ? view.blockedReason : null}
    </Notice>}
    {error && <Notice intent="error" onDismiss={() => setError(null)}>{error}</Notice>}
    {saved && <Notice intent="success" onDismiss={() => setSaved(null)}>{saved}</Notice>}

    <Card className={styles.card}>
      <Field label="Empresa" required>
        <Select value={companyId} disabled={busy} onChange={(_, data) => { setCompanyId(data.value); writeSelected(data.value); }}>
          {companies.map(c => <option key={c.companyId} value={c.companyId}>{c.legalName} — {c.cnpjFormatted}</option>)}
        </Select>
      </Field>
      <Switch checked={form.enabled} disabled={!view || busy} label="Monitoramento automático ativo"
        onChange={(_, data) => change({ enabled: data.checked })}/>
      <div className={styles.fields}>
        <Field label="Intervalo (minutos)" required validationState={intervalValid ? "none" : "error"}
          validationMessage={intervalValid ? undefined : "De 1 a 10080"} hint={intervalValid ? "Mínimo de 1 minuto" : undefined}>
          <Input value={form.interval} inputMode="numeric" disabled={!view || busy} onChange={(_, data) => change({ interval: data.value })}/>
        </Field>
        <Field label="Pasta de destino" required={form.enabled} hint="Pasta neste computador. Cada NF-e é gravada como <chave de acesso>.xml">
          <Input value={form.folder} placeholder="C:\NexFiscal\XML" spellCheck={false} autoComplete="off" disabled={!view || busy}
            onChange={(_, data) => change({ folder: data.value })}/>
        </Field>
      </div>
      <Caption1 className={styles.note}><InfoRegular fontSize={16}/>
        <span>O intervalo é a periodicidade desejada. Quando não há documentos novos, a SEFAZ só permite outra consulta depois de 1 hora:
        nesse caso o NexFiscal aguarda o horário liberado, mesmo que o intervalo seja menor.</span>
      </Caption1>
      <div className={styles.actions}>
        <Caption1 className={styles.hint}>{dirty.current ? "Há alterações não salvas." : ""}</Caption1>
        <Button appearance="primary" icon={busy ? <Spinner size="tiny"/> : <SaveRegular/>} disabled={!canSave} onClick={save}>Salvar configuração</Button>
      </div>
    </Card>

    {view && (view.lastRunAtUtc || view.lastMessage) && <Card className={styles.card}>
      <Subtitle2 as="h2">Última execução automática</Subtitle2>
      <Caption1 className={styles.meta}>{view.lastRunAtUtc ? formatDateTime(view.lastRunAtUtc) : "Ainda não executou"}{jobState ? ` · ${jobState.title}` : ""}</Caption1>
      {view.lastMessage && <Body1>{view.lastMessage}</Body1>}
      {job && <section className={styles.counters} aria-label="Resultado da última execução automática">
        {jobCounters(job).map(counter => <div key={counter.key} className={styles.counter} data-counter={counter.key}>
          <span className={mergeClasses(styles.value, styles[counter.tone])}>{counter.value}</span>
          <Caption1 className={styles.label}>{counter.label}</Caption1>
        </div>)}
      </section>}
    </Card>}

    <Caption1 className={styles.note}><InfoRegular fontSize={16}/>
      <span>O monitoramento funciona enquanto o serviço NexFiscal estiver em execução neste computador, mesmo com o navegador fechado.
      Se o serviço for encerrado, as capturas param e são retomadas quando ele for iniciado de novo.</span>
    </Caption1>
  </>;
}
