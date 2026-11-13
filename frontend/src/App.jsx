import React, { useEffect, useState } from "react";
import { Database, CheckCircle2, AlertCircle, LoaderCircle, Eye, EyeOff, RefreshCw, LockKeyhole } from "lucide-react";
import { api, credentials } from "./api.js";
import "./styles.css";

const EMPTY = { server: "", port: "30015", username: "", password: "" };

export default function App() {
  const [form, setForm] = useState(EMPTY);
  const [saved, setSaved] = useState(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState("");
  const [tested, setTested] = useState(false);
  const [visible, setVisible] = useState(false);
  const [notice, setNotice] = useState(null);

  async function refresh() {
    setLoading(true);
    try {
      setSaved(await api("/api/setup/hana/status"));
    } catch (error) { setNotice({ error: true, text: error.message }); }
    finally { setLoading(false); }
  }
  useEffect(() => { refresh(); }, []);

  function update(field, value) {
    setForm(current => ({ ...current, [field]: value }));
    setTested(false);
    setNotice(null);
  }
  const valid = form.server.trim() && /^\d+$/.test(form.port) &&
    +form.port >= 1 && +form.port <= 65535 && form.username.trim() && form.password;
  async function submit(kind) {
    setBusy(kind);
    setNotice(null);
    try {
      const result = await api("/api/setup/hana/" + kind,
        { method: "POST", body: JSON.stringify(credentials(form)) });
      if (kind === "test") {
        setTested(true);
        setNotice({
          error: false,
          text: result.schemaReady
            ? "Conexão estabelecida. Estrutura existente validada."
            : "Conexão estabelecida. Estrutura ainda não está completa: ao salvar, o NexFiscal criará somente as tabelas ausentes."
        });
      } else {
        setForm(EMPTY);
        setTested(false);
        setVisible(false);
        setNotice({ error: false, text: "Conexão salva e estrutura validada com sucesso." });
        await refresh();
      }
    } catch (error) {
      setTested(false);
      setNotice({ error: true, text: error.message });
    } finally { setBusy(""); }
  }

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
        <p>Configure o SAP HANA uma única vez. O NexFiscal valida a conexão, prepara as tabelas ausentes e protege as credenciais.</p>
      </div>
      <section className="panel">
        <div className="panel-heading">
          <div className="panel-icon"><Database size={22}/></div>
          <div><h2>Configuração SAP HANA</h2><p>Banco intermediário · schema BRAI_NEX</p></div>
        </div>
        <div className="status-line">
          {loading ? <><LoaderCircle className="spin" size={17}/> Verificando configuração...</>
            : saved?.connected && saved.schemaReady
              ? <><CheckCircle2 size={17} className="green"/> Conexão salva e estrutura validada</>
              : saved?.configured
                ? <><AlertCircle size={17} className="amber"/> {saved.message}</>
                : <><AlertCircle size={17} className="slate"/> {saved?.message || "Configuração pendente"}</>}
          <button className="refresh" type="button" onClick={refresh} disabled={loading || !!busy} title="Atualizar status"><RefreshCw size={17}/></button>
        </div>
        <form onSubmit={e => { e.preventDefault(); if (tested) submit("save"); }}>
          <div className="fields">
            <label>Servidor / host<input value={form.server} onChange={e=>update("server",e.target.value)} placeholder="hana.empresa.local" autoComplete="off" /></label>
            <label>Porta SQL<input value={form.port} onChange={e=>update("port",e.target.value)} inputMode="numeric" placeholder="30015"/></label>
            <label>Usuário do banco<input value={form.username} onChange={e=>update("username",e.target.value)} placeholder="NEXFISCAL_APP" autoComplete="off"/></label>
            <label>Senha<div className="password"><input value={form.password} onChange={e=>update("password",e.target.value)} type={visible?"text":"password"} autoComplete="new-password" placeholder="Senha do usuário"/><button aria-label={visible?"Ocultar senha":"Mostrar senha"} type="button" onClick={()=>setVisible(!visible)}>{visible?<EyeOff size={18}/>:<Eye size={18}/>}</button></div></label>
          </div>
          {notice && <div role="status" className={notice.error?"message error":"message success"}>{notice.text}</div>}
          <div className="actions">
            <button className="secondary" type="button" disabled={!valid || !!busy} onClick={()=>submit("test")}>
              {busy==="test"?<LoaderCircle className="spin" size={18}/>:<Database size={18}/>} Testar conexão
            </button>
            <button className="primary" type="submit" disabled={!valid || !tested || !!busy}>
              {busy==="save"?<LoaderCircle className="spin" size={18}/>:<CheckCircle2 size={18}/>} Salvar configuração
            </button>
          </div>
        </form>
      </section>
      <div className="footnote"><LockKeyhole size={18}/> As credenciais são salvas de forma protegida no servidor local. Esta versão não contém captura de XML ou cadastro de certificado A1.</div>
    </main>
  </div>;
}
