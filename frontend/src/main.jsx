import React, { useState } from "react";
import { createRoot } from "react-dom/client";
import {
  FluentProvider, webLightTheme, webDarkTheme, Button, Card, CardHeader,
  Field, Input, Dropdown, Option, MessageBar, MessageBarBody,
  Spinner, Text, Title3, Subtitle2, Divider, Badge, makeStyles, tokens
} from "@fluentui/react-components";
import { DocumentBulletList24Regular, ShieldKeyhole24Regular, Database24Regular,
  CheckmarkCircle24Regular, ArrowUpload24Regular, WeatherMoon24Regular,
  WeatherSunny24Regular } from "@fluentui/react-icons";

const useStyles = makeStyles({
  page: { minHeight: "100vh", backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground1 },
  header: { backgroundColor: tokens.colorNeutralBackground1, borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: "14px 32px", display: "flex", alignItems: "center", justifyContent: "space-between" },
  brand: { display: "flex", alignItems: "center", gap: "12px", fontWeight: 700, fontSize: "19px" },
  content: { maxWidth: "970px", margin: "0 auto", padding: "36px 20px 64px" },
  intro: { display: "flex", flexDirection: "column", gap: "8px", marginBottom: "25px" },
  layout: { display: "grid", gridTemplateColumns: "minmax(0, 1.5fr) minmax(260px, 1fr)", gap: "20px" },
  panel: { padding: "24px", display: "flex", flexDirection: "column", gap: "18px" },
  fieldset: { display: "flex", flexDirection: "column", gap: "15px" },
  muted: { color: tokens.colorNeutralForeground3 },
  detail: { display: "flex", justifyContent: "space-between", gap: "10px", flexWrap: "wrap",
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, paddingBottom: "10px" },
  steps: { display: "flex", alignItems: "center", gap: "10px", padding: "12px 0" },
  footer: { marginTop: "24px", color: tokens.colorNeutralForeground3, fontSize: "12px" }
});

function App() {
  const styles = useStyles();
  const [dark, setDark] = useState(false);
  const [file, setFile] = useState(null);
  const [password, setPassword] = useState("");
  const [environment, setEnvironment] = useState("1");
  const [nsu, setNsu] = useState("");
  const [identity, setIdentity] = useState(null);
  const [hana, setHana] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");
  React.useEffect(() => {
    fetch("/api/health").then(r => r.json()).then(x => setHana(Boolean(x.hanaConfigured))).catch(() => {});
  }, []);
  function formData(includeRegistration = false) {
    const data = new FormData();
    data.append("certificate", file);
    data.append("password", password);
    if (includeRegistration) {
      data.append("environment", environment);
      data.append("initialNsu", nsu);
    }
    return data;
  }
  async function call(path, register = false) {
    setBusy(true); setError(""); setSuccess("");
    try {
      const response = await fetch(path, { method: "POST", body: formData(register) });
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || result.detail || "Falha na operação.");
      if (register) setSuccess("Empresa cadastrada pausada, aguardando configuração de captura.");
      else setIdentity(result);
    } catch (e) { setError(e.message || "Erro desconhecido."); }
    finally { setBusy(false); }
  }
  const ready = file && password.length > 0;
  return <FluentProvider theme={dark ? webDarkTheme : webLightTheme}>
    <div className={styles.page}>
      <header className={styles.header}>
        <div className={styles.brand}><DocumentBulletList24Regular primaryFill={tokens.colorBrandForeground1}/> NexFiscal <Badge appearance="outline">Configuração inicial</Badge></div>
        <Button appearance="subtle" icon={dark ? <WeatherSunny24Regular/> : <WeatherMoon24Regular/>} onClick={() => setDark(!dark)} aria-label="Alternar tema"/>
      </header>
      <main className={styles.content}>
        <div className={styles.intro}><Title3>Cadastro de empresa</Title3>
          <Text className={styles.muted}>Importe o certificado A1 e deixe o NexFiscal identificar os dados do contribuinte.</Text></div>
        <div className={styles.layout}>
          <Card className={styles.panel}>
            <CardHeader image={<ShieldKeyhole24Regular/>} header={<Subtitle2>Certificado digital A1</Subtitle2>}
              description="O arquivo e a senha são utilizados para leitura local e não são gravados no cadastro."/>
            <Divider/>
            <div className={styles.fieldset}>
              <Field label="Arquivo A1 (.pfx ou .p12)" required hint={file?.name || "Escolha o certificado da empresa."}>
                <input type="file" accept=".pfx,.p12" onChange={e => { setFile(e.target.files?.[0] || null); setIdentity(null); }}
                  style={{width:"100%",padding:"11px",border:`1px solid ${tokens.colorNeutralStroke1}`,borderRadius:"6px",color:"inherit"}}/>
              </Field>
              <Field label="Senha do certificado" required><Input type="password" autoComplete="off" value={password} onChange={(_, d) => {setPassword(d.value);setIdentity(null);}}/></Field>
              <Button appearance="primary" icon={<ArrowUpload24Regular/>} disabled={!ready || busy} onClick={() => call("/api/certificates/inspect")}>
                {busy ? "Validando..." : "Validar certificado e identificar empresa"}
              </Button>
            </div>
            {busy && <Spinner size="tiny" label="Processando certificado..."/>}
            {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
            {success && <MessageBar intent="success"><MessageBarBody>{success}</MessageBarBody></MessageBar>}
            {identity && <>
              <Divider/>
              <Subtitle2><CheckmarkCircle24Regular/> Dados identificados</Subtitle2>
              {[
                ["Razão social",identity.legalName],["CNPJ",identity.cnpj],["UF",identity.state || "Não identificada — confirmar posteriormente"],
                ["Validade",new Date(identity.validUntil).toLocaleDateString("pt-BR")],["Emissor",identity.issuer]
              ].map(([name, value]) => <div key={name} className={styles.detail}><Text weight="semibold">{name}</Text><Text style={{maxWidth:"65%",overflowWrap:"anywhere"}}>{value}</Text></div>)}
              <MessageBar intent="warning"><MessageBarBody>Verificação de cadeia ICP-Brasil, revogação e autorização na SEFAZ ainda pendentes. A empresa será cadastrada pausada.</MessageBarBody></MessageBar>
              <Field label="Ambiente de cadastro"><Dropdown value={environment === "1" ? "Produção" : "Homologação"} selectedOptions={[environment]} onOptionSelect={(_,d) => setEnvironment(d.optionValue)}>
                <Option value="1">Produção</Option><Option value="2">Homologação</Option></Dropdown></Field>
              <Field label="Último NSU confirmado" hint="Use o checkpoint do sistema anterior. Não reinicie em zero sem validar a situação fiscal.">
                <Input value={nsu} onChange={(_,d)=>setNsu(d.value)} placeholder="Ex.: 000000000183248"/>
              </Field>
              <Button appearance="primary" disabled={!hana || !/^[0-9]{1,15}$/.test(nsu) || busy}
                onClick={() => call("/api/companies/register", true)}>Cadastrar empresa no HANA (pausada)</Button>
              {!hana && <Text className={styles.muted}>Cadastro indisponível: o driver e a conexão HANA devem ser configurados no serviço.</Text>}
            </>}
          </Card>
          <div>
            <Card className={styles.panel}>
              <CardHeader image={<Database24Regular/>} header={<Subtitle2>Preparação para captura</Subtitle2>}/>
              <div className={styles.steps}><CheckmarkCircle24Regular color={tokens.colorPaletteGreenForeground1}/><Text>Motor de processamento de XML</Text></div>
              <div className={styles.steps}><CheckmarkCircle24Regular color={tokens.colorPaletteGreenForeground1}/><Text>Cadastro intermediário HANA desenvolvido</Text></div>
              <div className={styles.steps}><Database24Regular/><Text>Conexão HANA: {hana ? "configurada" : "pendente de configuração"}</Text></div>
              <div className={styles.steps}><ShieldKeyhole24Regular/><Text>Certificado: {identity ? "analisado localmente" : "aguardando"}</Text></div>
              <Divider/>
              <Text className={styles.muted}>A consulta real à SEFAZ e o armazenamento seguro do A1 serão habilitados em uma etapa posterior.</Text>
            </Card>
          </div>
        </div>
        <footer className={styles.footer}>NexFiscal · Portal de configuração local · Microsoft Fluent UI</footer>
      </main>
    </div>
  </FluentProvider>;
}
createRoot(document.getElementById("root")).render(<App/>);
