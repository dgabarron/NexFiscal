import React, { useEffect, useState } from "react";
import { Database, CheckCircle2, AlertCircle, XCircle, LoaderCircle, Eye, EyeOff, RefreshCw, LockKeyhole } from "lucide-react";
import { api, credentials, describeStatus, isValid } from "./api.js";
import "./styles.css";

const EMPTY = { server: "", port: "30015", username: "", password: "" };
const ICONS = { ok: CheckCircle2, warn: AlertCircle, error: XCircle, idle: AlertCircle };

export default function App() {
  const [form, setForm] = useState(EMPTY);
  const [touched, setTouched] = useState(false);
  const [saved, setSaved] = useState(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState("");
  const [tested, setTested] = useState(false);
  const [visible, setVisible] = useState(false);
  const [replace, setReplace] = useState(false);
  const [notice, setNotice] = useState(null);

  async function refresh(keepForm) {
    setLoading(true);
    try {
      const status = await api("/api/setup/hana/status");
      setSaved(status);
      // Facilita o ajuste posterior: servidor, porta e usuário salvos voltam ao formulário. A senha nunca volta.
      if (!keepForm && status.server)
        setForm({ server: status.server, port: String(status.port), username: status.username, password: "" });
    } catch (error) {
      setSaved(null);
      setNotice({ tone: "error", text: error.message });
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

  async function submit(kind) {
    setBusy(kind);
    setNotice(null);
    try {
      const result = await api("/api/setup/hana/" + kind,
        { method: "POST", body: JSON.stringify(credentials(form, kind === "save" && unreadable && replace)) });
      if (kind === "test") {
        const blocked = result.schemaState === "incompatible";
        setTested(!blocked);
        setNotice({ tone: blocked ? "error" : result.schemaReady ? "success" : "warning", text: result.message,
          issues: blocked ? result.issues : [] });
      } else {
        setTested(false);
        setVisible(false);
        setReplace(false);
        setTouched(false);
        setNotice({ tone: "success", text: result.message + " Etapa 1 concluída." });
        await refresh(false);
      }
    } catch (error) {
      setTested(false);
      setNotice({ tone: "error", text: error.message, issues: error.issues });
    } finally { setBusy(""); }
  }

  const view = describeStatus(saved);
  const StatusIcon = ICONS[view.tone];

  return <div className="layout">
    <header className="header">
      <div className="brand-mark"><Database size={22}/></div>
      <div><strong>NexFiscal</strong><span>Configuração inicial</span></div>
      <div className="stage">V1 · Etapa 01 de 04</div>
    </header>
    <main>
      <div className="intro">
        <div className="eyebrow">ETAPA 1 · SAP HANA</div>
        <h1>Conexão com o banco de dados</h1>
        <p>Configure o SAP HANA uma única vez. O NexFiscal valida a conexão, cria somente as tabelas ausentes e protege as credenciais.</p>
      </div>
      <section className="panel">
        <div className="panel-heading">
          <div className="panel-icon"><Database size={22}/></div>
          <div><h2>Configuração SAP HANA</h2><p>Banco intermediário · schema BRAI_NEX</p></div>
        </div>
        <div className={"status-box " + (loading ? "idle" : view.tone)} role="status">
          <div className="status-line">
            {loading ? <><LoaderCircle className="spin" size={17}/> <strong>Verificando configuração...</strong></>
              : <><StatusIcon size={17}/> <strong>{view.title}</strong></>}
            <button className="refresh" type="button" onClick={() => refresh(touched)} disabled={loading || !!busy} title="Atualizar status" aria-label="Atualizar status"><RefreshCw size={17}/></button>
          </div>
          {!loading && saved?.message && saved.state !== "ready" && <p className="status-detail">{saved.message}</p>}
          {!loading && saved?.server && <p className="status-detail">Servidor <b>{saved.server}:{saved.port}</b> · Usuário <b>{saved.username}</b></p>}
          {!loading && saved?.issues?.length > 0 && <ul className="issues">{saved.issues.map(issue => <li key={issue}>{issue}</li>)}</ul>}
        </div>
        <form onSubmit={e => { e.preventDefault(); if (tested) submit("save"); }}>
          <div className="fields">
            <label>Servidor / host<input value={form.server} onChange={e=>update("server",e.target.value)} placeholder="hana.empresa.local" autoComplete="off" spellCheck="false"/></label>
            <label>Porta SQL<input value={form.port} onChange={e=>update("port",e.target.value)} inputMode="numeric" placeholder="30015"/></label>
            <label>Usuário do banco<input value={form.username} onChange={e=>update("username",e.target.value)} placeholder="NEXFISCAL_APP" autoComplete="off" spellCheck="false"/></label>
            <label>Senha<div className="password"><input value={form.password} onChange={e=>update("password",e.target.value)} type={visible?"text":"password"} autoComplete="new-password" placeholder={saved?.server ? "Digite a senha para testar ou alterar" : "Senha do usuário"}/><button aria-label={visible?"Ocultar senha":"Mostrar senha"} type="button" onClick={()=>setVisible(!visible)}>{visible?<EyeOff size={18}/>:<Eye size={18}/>}</button></div></label>
          </div>
          {unreadable && <label className="confirm">
            <input type="checkbox" checked={replace} onChange={e=>setReplace(e.target.checked)}/>
            <span>Quero substituir a configuração ilegível por uma nova. O arquivo anterior será preservado como backup, não apagado.</span>
          </label>}
          {notice && <div role="alert" className={"message " + notice.tone}>
            {notice.text}
            {notice.issues?.length > 0 && <ul className="issues">{notice.issues.map(issue => <li key={issue}>{issue}</li>)}</ul>}
          </div>}
          <div className="actions">
            <button className="secondary" type="button" disabled={!valid || !!busy} onClick={()=>submit("test")}>
              {busy==="test"?<LoaderCircle className="spin" size={18}/>:<Database size={18}/>} Testar conexão
            </button>
            <button className="primary" type="submit" disabled={!valid || !tested || !!busy || (unreadable && !replace)}
              title={tested ? "" : "Teste a conexão antes de salvar"}>
              {busy==="save"?<LoaderCircle className="spin" size={18}/>:<CheckCircle2 size={18}/>} Salvar configuração
            </button>
          </div>
        </form>
      </section>
      <div className="footnote"><LockKeyhole size={18}/> As credenciais são salvas de forma protegida neste computador e a senha nunca é exibida de volta. Esta versão não contém captura de XML nem cadastro de certificado A1.</div>
    </main>
  </div>;
}
