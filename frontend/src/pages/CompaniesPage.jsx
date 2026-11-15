import React, { useEffect, useState } from "react";
import { Avatar, Badge, Body1, Button, Caption1, Card, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Spinner, Subtitle2, Text, Title2, Tooltip, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { AddRegular, ArchiveRegular, ArrowSyncRegular, ArrowUploadRegular, BuildingRegular, CheckmarkCircleFilled, DeleteRegular, ShieldCheckmarkRegular, ShieldDismissRegular, ShieldErrorRegular } from "@fluentui/react-icons";
import { api, describeCertificate, removalWarning } from "../api.js";
import Notice from "../components/Notice.jsx";
import CertificateDialog from "../components/CertificateDialog.jsx";

const BADGE = {
  ok: { color: "success", icon: <ShieldCheckmarkRegular/> },
  warn: { color: "warning", icon: <ShieldErrorRegular/> },
  error: { color: "danger", icon: <ShieldDismissRegular/> },
};
const SELECTED_KEY = "nexfiscal.empresaSelecionada";

function readSelected() { try { return localStorage.getItem(SELECTED_KEY) || ""; } catch { return ""; } }
function writeSelected(id) { try { id ? localStorage.setItem(SELECTED_KEY, id) : localStorage.removeItem(SELECTED_KEY); } catch { /* preferência opcional */ } }

const useStyles = makeStyles({
  top: { display: "flex", alignItems: "flex-start", gap: tokens.spacingHorizontalL, flexWrap: "wrap" },
  heading: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXS, flexGrow: 1, minWidth: "240px" },
  lead: { color: tokens.colorNeutralForeground2, maxWidth: "640px" },
  tools: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS },
  list: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalM },
  company: {
    display: "grid", gridTemplateColumns: "auto 1fr auto", columnGap: tokens.spacingHorizontalL, rowGap: tokens.spacingVerticalM,
    alignItems: "start", padding: tokens.spacingHorizontalXL,
    "@media (max-width: 760px)": { gridTemplateColumns: "auto 1fr", padding: tokens.spacingHorizontalL },
  },
  selected: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: "-2px" },
  archived: { backgroundColor: tokens.colorNeutralBackground3 },
  info: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXS, minWidth: 0 },
  name: { overflowWrap: "anywhere" },
  meta: { color: tokens.colorNeutralForeground3 },
  badges: { display: "flex", gap: tokens.spacingHorizontalS, flexWrap: "wrap", marginTop: tokens.spacingVerticalXS },
  detail: { color: tokens.colorNeutralForeground2, overflowWrap: "anywhere" },
  actions: {
    display: "flex", flexDirection: "column", gap: tokens.spacingVerticalS, alignItems: "stretch", minWidth: "176px",
    "@media (max-width: 760px)": { gridColumn: "1 / -1", flexDirection: "row", flexWrap: "wrap", minWidth: 0 },
  },
  selectedMark: {
    display: "inline-flex", alignItems: "center", justifyContent: "center", gap: tokens.spacingHorizontalXS, minHeight: "32px",
    color: tokens.colorBrandForeground1, fontWeight: tokens.fontWeightSemibold,
  },
  sectionTitle: { marginTop: tokens.spacingVerticalM, color: tokens.colorNeutralForeground2 },
  empty: {
    display: "flex", flexDirection: "column", alignItems: "center", gap: tokens.spacingVerticalM, textAlign: "center",
    padding: `${tokens.spacingVerticalXXXL} ${tokens.spacingHorizontalL}`,
  },
  emptyIcon: { fontSize: "48px", color: tokens.colorNeutralForeground3 },
  loading: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS },
  footnote: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground3 },
  danger: { color: tokens.colorPaletteRedForeground1 },
});

export default function CompaniesPage({ hana, goToHana }) {
  const styles = useStyles();
  const [companies, setCompanies] = useState(null);
  const [loading, setLoading] = useState(false);
  const [form, setForm] = useState(null);         // null | {mode:"add"} | {mode:"update", company}
  const [removing, setRemoving] = useState(null); // empresa aguardando confirmação
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState(null);
  const [selected, setSelected] = useState(readSelected);

  const hanaReady = hana?.state === "ready";

  async function refresh() {
    setLoading(true);
    try { setCompanies(await api("/api/companies")); }
    catch (error) { setCompanies(null); setNotice({ intent: "error", text: error.message }); }
    finally { setLoading(false); }
  }
  useEffect(() => { if (hanaReady) refresh(); }, [hanaReady]);

  function select(id) { setSelected(id); writeSelected(id); }

  async function done(result) {
    setForm(null);
    await refresh();
    setNotice({ intent: "success", text: result.message });
  }

  async function remove() {
    const company = removing;
    setBusy(true); setNotice(null);
    try {
      const result = await api(`/api/companies/${encodeURIComponent(company.companyId)}`, { method: "DELETE" });
      if (selected === company.companyId) select("");
      setRemoving(null);
      await refresh();
      setNotice({ intent: "success", text: result.message });
    } catch (error) { setRemoving(null); setNotice({ intent: "error", text: error.message }); }
    finally { setBusy(false); }
  }

  const heading = <div className={styles.heading}>
    <Title2 as="h1">Empresas e certificados A1</Title2>
    <Body1 className={styles.lead}>Cada empresa é cadastrada pelo seu certificado A1. O arquivo fica protegido neste computador e nunca é exibido de volta.</Body1>
  </div>;

  if (!hanaReady)
    return <>
      {heading}
      {hana === undefined
        ? <div className={styles.loading} role="status"><Spinner size="tiny"/> <Text>Verificando a conexão SAP HANA...</Text></div>
        : <Notice intent="warning" title="Conexão SAP HANA necessária"
            actions={<Button appearance="primary" onClick={goToHana}>Abrir Conexão SAP HANA</Button>}>
            O cadastro de empresas é gravado no SAP HANA. {hana?.message || "Configure a conexão primeiro."}
          </Notice>}
    </>;

  const active = (companies || []).filter(c => !c.archived);
  const archived = (companies || []).filter(c => c.archived);
  const openForm = next => { setNotice(null); setForm(next); };

  const card = company => {
    const badge = describeCertificate(company);
    const isSelected = !company.archived && selected === company.companyId;
    return <Card key={company.companyId} as="article" data-company={company.cnpj} aria-label={company.legalName}
      className={mergeClasses(styles.company, isSelected && styles.selected, company.archived && styles.archived)}>
      <Avatar name={company.legalName} shape="square" size={48} color={company.archived ? "neutral" : "colorful"} aria-hidden="true"/>
      <div className={styles.info}>
        <Subtitle2 as="h3" className={styles.name}>{company.legalName}</Subtitle2>
        <Caption1 className={styles.meta}>CNPJ {company.cnpjFormatted} · UF {company.uf || "não identificada"} · {company.environment}</Caption1>
        <div className={styles.badges}>
          <Badge appearance="tint" color={BADGE[badge.tone].color} icon={BADGE[badge.tone].icon} size="large">{badge.label}</Badge>
        </div>
        <Text size={200} className={styles.detail}>{company.certificateMessage}</Text>
        {company.issuer && <Caption1 className={styles.meta}>Emissor: {company.issuer}</Caption1>}
      </div>
      <div className={styles.actions}>
        {!company.archived && (isSelected
          ? <span className={styles.selectedMark}><CheckmarkCircleFilled/> Selecionada</span>
          : <Button onClick={() => select(company.companyId)}>Selecionar</Button>)}
        <Button icon={<ArrowUploadRegular/>} onClick={() => openForm({ mode: "update", company })}>{company.archived ? "Reativar com A1" : "Atualizar A1"}</Button>
        {!company.archived && <Button appearance="subtle" className={styles.danger} icon={<DeleteRegular/>} onClick={() => { setNotice(null); setRemoving(company); }}>Remover empresa</Button>}
      </div>
    </Card>;
  };

  return <>
    <div className={styles.top}>
      {heading}
      <div className={styles.tools}>
        <Tooltip content="Atualizar lista" relationship="label">
          <Button appearance="subtle" icon={loading ? <Spinner size="tiny"/> : <ArrowSyncRegular/>} disabled={loading} onClick={refresh}/>
        </Tooltip>
        <Button appearance="primary" icon={<AddRegular/>} onClick={() => openForm({ mode: "add" })}>Adicionar empresa</Button>
      </div>
    </div>

    {notice && <Notice intent={notice.intent} onDismiss={() => setNotice(null)}>{notice.text}</Notice>}

    <section className={styles.list} aria-label="Empresas cadastradas">
      {companies === null && loading && <div className={styles.loading} role="status"><Spinner size="tiny"/> <Text>Carregando empresas...</Text></div>}
      {companies !== null && companies.length === 0 && <Card className={styles.empty}>
        <BuildingRegular className={styles.emptyIcon}/>
        <Subtitle2>Nenhuma empresa cadastrada.</Subtitle2>
        <Body1 className={styles.lead}>Adicione a primeira empresa enviando o certificado A1 dela.</Body1>
        <Button appearance="primary" icon={<AddRegular/>} onClick={() => openForm({ mode: "add" })}>Adicionar empresa</Button>
      </Card>}
      {active.map(card)}
      {archived.length > 0 && <Subtitle2 as="h2" className={styles.sectionTitle}>Arquivadas</Subtitle2>}
      {archived.map(card)}
    </section>

    {selected && active.some(c => c.companyId === selected) &&
      <Caption1 className={styles.footnote}><CheckmarkCircleFilled fontSize={16}/> A empresa selecionada é a que abre primeiro na seção Captura de NF-e.</Caption1>}

    {form && <CertificateDialog key={form.mode + (form.company?.companyId || "")} mode={form.mode} company={form.company} onDone={done} onCancel={() => setForm(null)}/>}

    {removing && <Dialog open modalType="alert" onOpenChange={(_, data) => { if (!data.open && !busy) setRemoving(null); }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{removing.hasFiscalHistory ? "Arquivar empresa?" : "Remover empresa?"}</DialogTitle>
          <DialogContent>{removalWarning(removing)}</DialogContent>
          <DialogActions>
            <Button appearance="secondary" disabled={busy} onClick={() => setRemoving(null)}>Cancelar</Button>
            <Button appearance="primary" disabled={busy} onClick={remove}
              icon={busy ? <Spinner size="tiny"/> : removing.hasFiscalHistory ? <ArchiveRegular/> : <DeleteRegular/>}>
              {removing.hasFiscalHistory ? "Confirmar arquivamento" : "Confirmar remoção"}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>}
  </>;
}
