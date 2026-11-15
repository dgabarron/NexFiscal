import React, { useState } from "react";
import { Database, Building2 } from "lucide-react";
import HanaPage from "./pages/HanaPage.jsx";
import CompaniesPage from "./pages/CompaniesPage.jsx";
import "./styles.css";

export default function App() {
  const [tab, setTab] = useState("hana");
  const [hana, setHana] = useState(undefined); // undefined = ainda carregando

  // Ao abrir: se o HANA já está pronto, a tela útil é a de empresas.
  function onStatus(status) {
    setHana(previous => {
      if (previous === undefined && status?.state === "ready") setTab("companies");
      return status;
    });
  }

  return <div className="layout">
    <header className="header">
      <div className="brand-mark"><Database size={22}/></div>
      <div><strong>NexFiscal</strong><span>Captura de NF-e</span></div>
      <nav className="tabs" aria-label="Seções">
        <button type="button" className={tab==="hana"?"tab active":"tab"} onClick={()=>setTab("hana")}><Database size={16}/> Conexão SAP HANA</button>
        <button type="button" className={tab==="companies"?"tab active":"tab"} onClick={()=>setTab("companies")}><Building2 size={16}/> Empresas</button>
      </nav>
      <div className="stage">V1 · Etapa 02 de 04</div>
    </header>
    <main>
      {/* A aba HANA permanece montada para não perder o que foi digitado ao trocar de aba. */}
      <div hidden={tab !== "hana"}><HanaPage onStatus={onStatus}/></div>
      {tab === "companies" && <CompaniesPage hana={hana} goToHana={()=>setTab("hana")}/>}
    </main>
  </div>;
}
