import React, { useEffect, useState } from "react";
import { Button, Caption1, Card, Checkbox, Field, Input, Spinner, Subtitle2, Body1, Text, Title2, Tooltip, makeStyles, tokens } from "@fluentui/react-components";
import { ArrowSyncRegular, EditRegular, LockClosedRegular, PlugConnectedRegular, SaveRegular, WrenchRegular } from "@fluentui/react-icons";
import { api, credentials, describeStatus, isValid } from "../api.js";
import Notice from "../components/Notice.jsx";
import PasswordInput from "../components/PasswordInput.jsx";

const EMPTY = { server: "", port: "30015", username: "", password: "" };
const INTENT = { ok: "success", warn: "warning", error: "error", idle: "info" };

const useStyles = makeStyles({
  top: { display: "flex", alignItems: "flex-start", gap: tokens.spacingHorizontalL },
  heading: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXS, flexGrow: 1 },
  lead: { color: tokens.colorNeutralForeground2, maxWidth: "640px" },
  card: { padding: tokens.spacingHorizontalXXL, gap: tokens.spacingVerticalL, "@media (max-width: 760px)": { padding: tokens.spacingHorizontalL } },
  cardHeader: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalM },
  grow: { flexGrow: 1 },
  loading: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS },
  summary: {
    display: "grid", gridTemplateColumns: "max-content 1fr", columnGap: tokens.spacingHorizontalXXL, rowGap: tokens.spacingVerticalS, margin: 0,
    "& dt": { color: tokens.colorNeutralForeground3 }, "& dd": { margin: 0, fontWeight: tokens.fontWeightSemibold, overflowWrap: "anywhere" },
  },
  fields: {
    display: "grid", gridTemplateColumns: "2fr 1fr", alignItems: "start", gap: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL}`,
    "@media (max-width: 760px)": { gridTemplateColumns: "1fr" },
  },
  actions: {
    display: "flex", justifyContent: "flex-end", alignItems: "center", gap: tokens.spacingHorizontalM, flexWrap: "wrap",
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`, paddingTop: tokens.spacingVerticalL,
  },
  hint: { color: tokens.colorNeutralForeground3, marginRight: "auto" },
  footnote: { display: "flex", alignItems: "flex-start", gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground3 },
});

export default function HanaPage({ onStatus }) {
  const styles = useStyles();
  const [form, setForm] = useState(EMPTY);
  const [touched, setTouched] = useState(false);
  const [editing, setEditing] = useState(false);
  const [saved, setSaved] = useState(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState("");
  const [tested, setTested] = useState(false);
  const [replace, setReplace] = useState(false);
  const [notice, setNotice] = useState(null);

  async function refresh(keepForm) {
    setLoading(true);
    try {
      const status = await api("/api/setup/hana/status");
      setSaved(status);
      onStatus?.(status);
      // Facilita o ajuste posterior: servidor, porta e usuário salvos voltam ao formulário. A senha nunca volta.
      if (!keepForm && status.server)
        setForm({ server: status.server, port: String(status.port), username: status.username, password: "" });
    } catch (error) {
      setSaved(null);
      onStatus?.(null);
      setNotice({ intent: "error", text: error.message });
    } finally { setLoading(false); }
  }
  useEffect(() => { refresh(false); }, []);

  function update(field, value) {
    setForm(current => ({ ...current, [field]: value }));
    setTouched(true);
    setTested(false);
    setNotice(null);
  }

  const unreadable = saved?.state === "unreadable";
  const valid = isValid(form);
  const portInvalid = form.port !== "" && !(/^\d+$/.test(form.port) && +form.port >= 1 && +form.port <= 65535);
  const showForm = editing || saved?.state !== "ready";

  async function upgrade() {
    setBusy("upgrade");
    setNotice(null);
    try {
      const result = await api("/api/setup/hana/upgrade", { method: "POST" });
      setNotice({ intent: "success", text: result.message });
      await refresh(touched);
    } catch (error) {
      setNotice({ intent: "error", text: error.message, issues: error.issues });
    } finally { setBusy(""); }
  }

  async function submit(kind) {
    setBusy(kind);
    setNotice(null);
    try {
      const result = await api("/api/setup/hana/" + kind,
        { method: "POST", body: JSON.stringify(credentials(form, kind === "save" && unreadable && replace)) });
      if (kind === "test") {
        const blocked = result.schemaState === "incompatible";
        setTested(!blocked);
        setNotice({ intent: blocked ? "error" : result.schemaReady ? "success" : "warning", text: result.message,
          issues: blocked ? result.issues : [] });
      } else {
        setTested(false);
        setReplace(false);
        setTouched(false);
        setEditing(false);
        setNotice({ intent: "success", text: result.message + " Agora você pode cadastrar empresas na seção Empresas." });
        await refresh(false);
      }
    } catch (error) {
      setTested(false);
      setNotice({ intent: "error", text: error.message, issues: error.issues });
    } finally { setBusy(""); }
  }

  const view = describeStatus(saved);
  const refreshButton = <Tooltip content="Verificar novamente" relationship="label">
    <Button appearance="subtle" icon={<ArrowSyncRegular/>} disabled={loading || !!busy} onClick={() => refresh(touched)}/>
  </Tooltip>;

  return <>
    <div className={styles.top}>
      <div className={styles.heading}>
        <Title2 as="h1">Conexão SAP HANA</Title2>
        <Body1 className={styles.lead}>Informe o banco SAP HANA do SAP Business One uma única vez. O NexFiscal valida a conexão, cria somente as tabelas ausentes do schema BRAI_NEX e protege as credenciais.</Body1>
      </div>
      {refreshButton}
    </div>

    {loading
      ? <div className={styles.loading} role="status"><Spinner size="tiny"/> <Text>Verificando configuração...</Text></div>
      : <Notice intent={INTENT[view.tone]} title={view.title} issues={saved?.issues}
          actions={saved?.canUpgrade && <Button appearance="primary" icon={busy === "upgrade" ? <Spinner size="tiny"/> : <WrenchRegular/>} disabled={!!busy} onClick={upgrade}>Atualizar estrutura</Button>}>
          {saved?.state !== "ready" && saved?.message}
        </Notice>}

    {notice && <Notice intent={notice.intent} issues={notice.issues} onDismiss={() => setNotice(null)}>{notice.text}</Notice>}

    {!loading && !showForm && <Card className={styles.card}>
      <div className={styles.cardHeader}>
        <Subtitle2 as="h2" className={styles.grow}>Conexão salva</Subtitle2>
        <Button icon={<EditRegular/>} onClick={() => setEditing(true)}>Alterar conexão</Button>
      </div>
      <dl className={styles.summary}>
        <dt>Servidor</dt><dd>{saved.server}</dd>
        <dt>Porta SQL</dt><dd>{saved.port}</dd>
        <dt>Usuário</dt><dd>{saved.username}</dd>
        <dt>Senha</dt><dd>Protegida — nunca é exibida</dd>
      </dl>
    </Card>}

    {!loading && showForm && <Card className={styles.card}>
      <div className={styles.cardHeader}>
        <Subtitle2 as="h2" className={styles.grow}>{saved?.server ? "Alterar conexão" : "Dados da conexão"}</Subtitle2>
        {editing && <Button appearance="subtle" disabled={!!busy} onClick={() => { setEditing(false); setNotice(null); setTested(false); refresh(false); }}>Cancelar</Button>}
      </div>
      <form onSubmit={e => { e.preventDefault(); if (tested) submit("save"); }}>
        <div className={styles.fields}>
          <Field label="Servidor / host" required hint="Nome ou IP do servidor, sem a porta">
            <Input value={form.server} onChange={(_, d) => update("server", d.value)} placeholder="hana.empresa.local" autoComplete="off" spellCheck={false}/>
          </Field>
          <Field label="Porta SQL" required hint="Padrão do SAP B1: 30015" validationState={portInvalid ? "error" : "none"} validationMessage={portInvalid ? "Entre 1 e 65535" : undefined}>
            <Input value={form.port} onChange={(_, d) => update("port", d.value)} inputMode="numeric" placeholder="30015"/>
          </Field>
          <Field label="Usuário do banco" required>
            <Input value={form.username} onChange={(_, d) => update("username", d.value)} placeholder="NEXFISCAL_APP" autoComplete="off" spellCheck={false}/>
          </Field>
          <Field label="Senha" required>
            <PasswordInput value={form.password} onChange={value => update("password", value)}
              placeholder={saved?.server ? "Digite a senha para testar" : "Senha do usuário"}/>
          </Field>
        </div>
        {unreadable && <Checkbox checked={replace} onChange={(_, d) => setReplace(!!d.checked)} style={{ marginTop: tokens.spacingVerticalM }}
          label="Quero substituir a configuração ilegível por uma nova. O arquivo anterior será preservado como backup, não apagado."/>}
        <div className={styles.actions} style={{ marginTop: tokens.spacingVerticalXL }}>
          <Caption1 className={styles.hint}>{tested ? "Conexão testada. Você já pode salvar." : "Teste a conexão para habilitar o salvamento."}</Caption1>
          <Button type="button" icon={busy === "test" ? <Spinner size="tiny"/> : <PlugConnectedRegular/>} disabled={!valid || !!busy} onClick={() => submit("test")}>Testar conexão</Button>
          <Button type="submit" appearance="primary" icon={busy === "save" ? <Spinner size="tiny"/> : <SaveRegular/>}
            disabled={!valid || !tested || !!busy || (unreadable && !replace)}>Salvar configuração</Button>
        </div>
      </form>
    </Card>}

    <Caption1 className={styles.footnote}><LockClosedRegular fontSize={16}/> As credenciais ficam protegidas neste computador e a senha nunca é exibida de volta.</Caption1>
  </>;
}
