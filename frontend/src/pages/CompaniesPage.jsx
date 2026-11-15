import React, { useEffect, useRef, useState } from "react";
import { Building2, Plus, ShieldCheck, ShieldAlert, ShieldX, LoaderCircle, RefreshCw, Trash2, Upload, Check, Eye, EyeOff, X, Archive } from "lucide-react";
import { api, describeCertificate, fileToBase64, formatDate, removalWarning } from "../api.js";

const BADGE_ICONS = { ok: ShieldCheck, warn: ShieldAlert, error: ShieldX };
const SELECTED_KEY = "nexfiscal.empresaSelecionada";

function readSelected() { try { return localStorage.getItem(SELECTED_KEY) || ""; } catch { return ""; } }
function writeSelected(id) { try { id ? localStorage.setItem(SELECTED_KEY, id) : localStorage.removeItem(SELECTED_KEY); } catch { /* preferência opcional */ } }

/** Formulário de envio do A1: usado para cadastrar (mode "add") e para atualizar/reativar (mode "update"). */
function CertificateForm({ mode, company, onDone, onCancel }) {
  const fileInput = useRef(null);
  const [file, setFile] = useState(null);
  const [password, setPassword] = useState("");
  const [visible, setVisible] = useState(false);
  const [preview, setPreview] = useState(null);
  const [replace, setReplace] = useState(false);
  const [busy, setBusy] = useState("");
  const [error, setError] = useState(null);

  const unreadable = company?.certificateStatus === "unreadable";
  const ready = file && password;
  const sameCompany = mode === "add" ? !preview?.existingCompanyId : preview?.cnpj === company.cnpj;

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
      const result = mode === "add"
        ? await api("/api/companies", { method: "POST", body: await body() })
        : await api(`/api/companies/${encodeURIComponent(company.companyId)}/certificate`, { method: "PUT", body: await body() });
      onDone(result);
    } catch (e) { setError(e.message); }
    finally { setBusy(""); }
  }

  const title = mode === "add" ? "Adicionar empresa" : company.archived ? `Reativar ${company.legalName}` : `Atualizar A1 de ${company.legalName}`;
  const confirmLabel = mode === "add" ? "Cadastrar empresa" : company.archived ? "Reativar empresa" : "Atualizar certificado";

  return <section className="panel form-panel" aria-label={title}>
    <div className="panel-heading">
      <div className="panel-icon"><Upload size={22}/></div>
      <div><h2>{title}</h2><p>{mode === "add"
        ? "CNPJ e razão social são lidos do próprio certificado. Nada é digitado."
        : `O novo certificado precisa ser do CNPJ ${company.cnpjFormatted}. Cadastro e histórico são preservados.`}</p></div>
      <button className="icon-button" type="button" onClick={onCancel} aria-label="Fechar"><X size={18}/></button>
    </div>
    <div className="fields">
      <label>Arquivo do certificado A1 (.pfx ou .p12)
        <input ref={fileInput} type="file" accept=".pfx,.p12,application/x-pkcs12" onChange={e => { setFile(e.target.files[0] || null); reset(); }}/>
      </label>
      <label>Senha do certificado
        <div className="password"><input value={password} onChange={e => { setPassword(e.target.value); reset(); }} type={visible?"text":"password"} autoComplete="new-password" placeholder="Senha do A1"/>
          <button aria-label={visible?"Ocultar senha":"Mostrar senha"} type="button" onClick={()=>setVisible(!visible)}>{visible?<EyeOff size={18}/>:<Eye size={18}/>}</button></div>
      </label>
    </div>
    {unreadable && <label className="confirm">
      <input type="checkbox" checked={replace} onChange={e=>setReplace(e.target.checked)}/>
      <span>O certificado guardado não pode ser lido. Quero substituí-lo; o arquivo anterior será preservado como backup, não apagado.</span>
    </label>}
    {error && <div role="alert" className="message error">{error}</div>}
    {preview && <div className={"preview " + (sameCompany ? "ok" : "blocked")} role="status">
      <strong>{preview.message}</strong>
      <dl>
        <dt>CNPJ</dt><dd>{preview.cnpjFormatted}</dd>
        <dt>Razão social</dt><dd>{preview.legalName}</dd>
        <dt>UF</dt><dd>{preview.uf || "Não identificada no certificado"}</dd>
        <dt>Emissor</dt><dd>{preview.issuer}</dd>
        <dt>Validade</dt><dd>{formatDate(preview.notBeforeUtc)} a {formatDate(preview.notAfterUtc)} ({preview.daysRemaining} dias restantes)</dd>
      </dl>
      {mode === "update" && !sameCompany && <p className="blocked-text">Este certificado é de outro CNPJ e não pode ser usado nesta empresa.</p>}
    </div>}
    <div className="actions">
      <button className="secondary" type="button" onClick={onCancel} disabled={!!busy}>Cancelar</button>
      {!preview || !sameCompany
        ? <button className="primary" type="button" disabled={!ready || !!busy} onClick={validate}>
            {busy==="validate"?<LoaderCircle className="spin" size={18}/>:<ShieldCheck size={18}/>} Validar certificado</button>
        : <button className="primary" type="button" disabled={!!busy || (unreadable && !replace)} onClick={confirm}>
            {busy==="confirm"?<LoaderCircle className="spin" size={18}/>:<Check size={18}/>} {confirmLabel}</button>}
    </div>
  </section>;
}

export default function CompaniesPage({ hana, goToHana }) {
  const [companies, setCompanies] = useState(null);
  const [loading, setLoading] = useState(false);
  const [form, setForm] = useState(null);         // null | {mode:"add"} | {mode:"update", company}
  const [removing, setRemoving] = useState("");   // companyId aguardando confirmação
  const [busy, setBusy] = useState("");
  const [notice, setNotice] = useState(null);
  const [selected, setSelected] = useState(readSelected);

  const hanaReady = hana?.state === "ready";

  async function refresh() {
    setLoading(true);
    try { setCompanies(await api("/api/companies")); }
    catch (error) { setCompanies(null); setNotice({ tone: "error", text: error.message }); }
    finally { setLoading(false); }
  }
  useEffect(() => { if (hanaReady) refresh(); }, [hanaReady]);

  function select(id) { setSelected(id); writeSelected(id); }

  async function done(result) {
    setForm(null);
    setNotice({ tone: "success", text: result.message });
    await refresh();
  }

  async function remove(company) {
    setBusy(company.companyId); setNotice(null);
    try {
      const result = await api(`/api/companies/${encodeURIComponent(company.companyId)}`, { method: "DELETE" });
      if (selected === company.companyId) select("");
      setNotice({ tone: "success", text: result.message });
      setRemoving("");
      await refresh();
    } catch (error) { setNotice({ tone: "error", text: error.message }); }
    finally { setBusy(""); }
  }

  if (!hanaReady)
    return <>
      <div className="intro"><div className="eyebrow">EMPRESAS</div><h1>Empresas e certificados A1</h1></div>
      <section className="panel"><div className="status-box warn" role="status">
        <div className="status-line"><ShieldAlert size={17}/> <strong>{hana === undefined ? "Verificando a conexão SAP HANA..." : "Conexão SAP HANA necessária"}</strong></div>
        {hana !== undefined && <>
          <p className="status-detail">O cadastro de empresas é gravado no SAP HANA. {hana?.message || "Configure a conexão primeiro."}</p>
          <button className="primary inline" type="button" onClick={goToHana}>Abrir Conexão SAP HANA</button>
        </>}
      </div></section>
    </>;

  const active = (companies || []).filter(c => !c.archived);
  const archived = (companies || []).filter(c => c.archived);

  const card = company => {
    const badge = describeCertificate(company);
    const BadgeIcon = BADGE_ICONS[badge.tone];
    const isSelected = selected === company.companyId;
    return <article key={company.companyId} className={"company" + (isSelected ? " selected" : "") + (company.archived ? " archived" : "")}>
      <div className="company-main">
        <h3>{company.legalName}</h3>
        <p className="company-meta">CNPJ {company.cnpjFormatted} · UF {company.uf || "não identificada"} · {company.environment}</p>
        <p className={"badge " + badge.tone}><BadgeIcon size={15}/> {badge.label}</p>
        <p className="company-detail">{company.certificateMessage}</p>
        {company.issuer && <p className="company-detail muted">Emissor: {company.issuer}</p>}
      </div>
      <div className="company-actions">
        {!company.archived && (isSelected
          ? <span className="selected-mark"><Check size={15}/> Selecionada</span>
          : <button className="secondary small" type="button" onClick={()=>select(company.companyId)}>Selecionar</button>)}
        <button className="secondary small" type="button" onClick={()=>{ setNotice(null); setRemoving(""); setForm({ mode: "update", company }); }}>
          <Upload size={15}/> {company.archived ? "Reativar com A1" : "Atualizar A1"}</button>
        {!company.archived && <button className="danger small" type="button" onClick={()=>{ setForm(null); setRemoving(company.companyId); }}>
          <Trash2 size={15}/> Remover empresa</button>}
      </div>
      {removing === company.companyId && <div className="remove-confirm" role="alertdialog" aria-label="Confirmar remoção">
        <p>{removalWarning(company)}</p>
        <div className="actions tight">
          <button className="secondary small" type="button" disabled={!!busy} onClick={()=>setRemoving("")}>Cancelar</button>
          <button className="danger small solid" type="button" disabled={!!busy} onClick={()=>remove(company)}>
            {busy===company.companyId?<LoaderCircle className="spin" size={15}/>:company.hasFiscalHistory?<Archive size={15}/>:<Trash2 size={15}/>}
            {company.hasFiscalHistory ? "Confirmar arquivamento" : "Confirmar remoção"}</button>
        </div>
      </div>}
    </article>;
  };

  return <>
    <div className="intro">
      <div className="eyebrow">EMPRESAS</div>
      <h1>Empresas e certificados A1</h1>
      <p>Cada empresa é cadastrada pelo seu certificado A1. O arquivo fica guardado de forma protegida neste computador e nunca é exibido de volta.</p>
    </div>
    <div className="toolbar">
      <button className="primary" type="button" onClick={()=>{ setNotice(null); setRemoving(""); setForm({ mode: "add" }); }}><Plus size={18}/> Adicionar empresa</button>
      <button className="refresh" type="button" onClick={refresh} disabled={loading} title="Atualizar lista" aria-label="Atualizar lista"><RefreshCw size={17} className={loading?"spin":""}/></button>
    </div>
    {notice && <div role="alert" className={"message " + notice.tone}>{notice.text}</div>}
    {form && <CertificateForm key={form.mode + (form.company?.companyId || "")} mode={form.mode} company={form.company} onDone={done} onCancel={()=>setForm(null)}/>}
    <section className="company-list" aria-label="Empresas cadastradas">
      {companies === null && loading && <p className="empty"><LoaderCircle className="spin" size={17}/> Carregando empresas...</p>}
      {companies !== null && companies.length === 0 && <div className="empty-box"><Building2 size={28}/><p>Nenhuma empresa cadastrada.</p><p className="muted">Clique em "Adicionar empresa" e envie o certificado A1.</p></div>}
      {active.map(card)}
      {archived.length > 0 && <h2 className="section-title">Arquivadas</h2>}
      {archived.map(card)}
    </section>
    {selected && active.some(c => c.companyId === selected) &&
      <div className="footnote"><Check size={18}/> A empresa selecionada será usada na captura de NF-e, disponível na próxima etapa.</div>}
  </>;
}
