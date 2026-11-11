import React, { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { Database, Building2, Activity, FileText, ShieldCheck, CheckCircle2, ArrowRight,
  LockKeyhole, Server, Eye, EyeOff, RefreshCw, ChevronRight, BadgeCheck, AlertCircle,
  CloudUpload, PlugZap, CircleDot, LayoutDashboard } from "lucide-react";
import { Button } from "./components/ui/button";
import { Input } from "./components/ui/input";
import { Card, CardHeader, CardContent } from "./components/ui/card";
import "./index.css";
import { formatCnpj, formatCpf, issuerDisplayName } from "./lib/fiscal-format";

const steps=[
  {number:"01",label:"Conexão SAP HANA",helper:"Infraestrutura",icon:Database},
  {number:"02",label:"Empresa e certificado",helper:"Identificação fiscal",icon:Building2},
  {number:"03",label:"Operação",helper:"Distribuição de documentos",icon:Activity}
];
const details=(label,value)=><div className="flex items-start justify-between gap-5 border-b border-slate-100 py-3.5 last:border-0">
  <span className="text-sm text-slate-500">{label}</span><span className="text-right text-sm font-medium text-slate-800 break-words max-w-[65%]">{value||"Não identificado"}</span>
</div>;
function Notice({type="warning",children}){return <div className={`flex items-start gap-3 rounded-xl border p-3.5 text-sm leading-relaxed ${type==="error"?"bg-red-50 border-red-200 text-red-800":type==="success"?"bg-emerald-50 border-emerald-200 text-emerald-800":"bg-amber-50 border-amber-200 text-amber-900"}`}><span className="mt-0.5">{type==="success"?<CheckCircle2 size={17}/>:<AlertCircle size={17}/>}</span><div>{children}</div></div>}
function Label({children}){return <label className="mb-2 block text-sm font-semibold text-slate-700">{children}</label>}

function App(){
 const [stage,setStage]=useState(0);
 const [hanaReady,setHanaReady]=useState(false);
 const [hana,setHana]=useState({server:"",port:"30015",username:"",password:""});
 const [hanaChecked,setHanaChecked]=useState(false);
 const [hanaTablesFound,setHanaTablesFound]=useState(null);
 const [showPassword,setShowPassword]=useState(false);
 const [file,setFile]=useState(null),[password,setPassword]=useState("");
 const [identity,setIdentity]=useState(null),[registered,setRegistered]=useState(null);
 const [busy,setBusy]=useState(false),[error,setError]=useState(""),[message,setMessage]=useState("");
 const [captureState,setCaptureState]=useState(null),[captureBusy,setCaptureBusy]=useState(false);
 const [captureResult,setCaptureResult]=useState(null);
 const [connectivity,setConnectivity]=useState({});
 useEffect(()=>{fetch("/api/health").then(r=>r.json()).then(async x=>{
    if(!x.hanaConfigured)return;
    setHanaReady(true);
    try{
      const resp=await fetch("/api/capture/status");
      if(resp.ok){
        const state=await resp.json();
        setCaptureState(state);
        if(state.companies?.length){setRegistered({companyId:state.companies[0].companyId});setStage(2);}
        else setStage(1);
      }else setStage(1);
    }catch{setStage(1);}
  }).catch(()=>{});},[]);
 function navigate(n){if(n===0||n===1&&hanaReady||n===2&&registered){setStage(n);setError("");setMessage("");}}
 function setH(field,value){setHana(x=>({...x,[field]:value}));setHanaChecked(false);setHanaTablesFound(null);setMessage("");setError("");}
 async function invoke(path,data){
   const resp=await fetch(path,{method:"POST",headers:data instanceof FormData?undefined:{"Content-Type":"application/json"},body:data instanceof FormData?data:JSON.stringify(data)});
   const result=await resp.json().catch(()=>({}));
   if(!resp.ok)throw new Error((result.error||result.detail||"A operação não foi concluída.")+(result.diagnostico?` (Diagnóstico: ${result.diagnostico}; etapa: ${result.etapa})`:""));
   return result;
 }
 async function checkHana(save=false){
  setBusy(true);setError("");setMessage("");
  try{
   const result=await invoke(save?"/api/setup/hana/save":"/api/setup/hana/test",
     {server:hana.server,port:Number(hana.port),username:hana.username,password:hana.password});
   setHanaChecked(result.schemaReady);
   setHanaTablesFound(result.tablesFound);
   if(!result.schemaReady)throw new Error(`Faltam tabelas no HANA: encontradas ${result.tablesFound} de 5 tabelas BRAI_NEX. Clique em "Preparar estrutura" para criar as tabelas ausentes.`);
   if(save){setHanaReady(true);setHana(x=>({...x,password:""}));setStage(1);setMessage("Conexão HANA validada e salva de forma protegida no servidor.");}
   else setMessage("Conexão estabelecida. Schema BRAI_NEX e cinco tabelas verificados.");
  }catch(e){setError(e.message);}
  finally{setBusy(false);}
 }
 async function provisionHana(){
   setBusy(true);setError("");setMessage("");
   try{
     const result=await invoke("/api/setup/hana/provision",
       {server:hana.server,port:Number(hana.port),username:hana.username,password:hana.password});
     setHanaTablesFound(result.tablesFound);
     setHanaChecked(result.schemaReady);
     if(!result.schemaReady)throw new Error("Instalação incompleta. Consulte o administrador do HANA.");
     setMessage("Estrutura BRAI_NEX preparada e validada. Clique em Salvar e continuar.");
   }catch(e){setError(e.message);}finally{setBusy(false);}
 }
 function certPayload(){const d=new FormData();d.append("certificate",file);d.append("password",password);return d;}
 async function inspect(register=false){
  setBusy(true);setError("");setMessage("");
  try{
   const result=await invoke(register?"/api/companies/register":"/api/certificates/inspect",certPayload());
   if(register){setRegistered(result);setPassword("");setFile(null);setStage(2);await refreshCapture();setMessage("Empresa cadastrada e certificado A1 armazenado de forma protegida. A captura permanece pausada até a inicialização segura da SEFAZ.");}
   else setIdentity(result);
  }catch(e){setError(e.message);}finally{setBusy(false);}
 }
 async function refreshCapture(){
  try{
    const resp=await fetch("/api/capture/status");
    if(!resp.ok)throw new Error("Falha ao consultar HANA.");
    setCaptureState(await resp.json());
  }catch(e){setError(e.message);}
 }
 async function testConnectivity(companyId){
  setCaptureBusy(true);setError("");setMessage("");
  try{
    const result=await invoke("/api/capture/test-connectivity",{companyId});
    setConnectivity(x=>({...x,[companyId]:result}));
  }catch(e){setError(e.message);}finally{setCaptureBusy(false);}
 }
 async function downloadNfe(companyId){
  if(!window.confirm("O sistema anterior de captura NF-e está desligado para este CNPJ? Esta ação enviará uma consulta REAL à SEFAZ. Se não existir checkpoint, o NexFiscal iniciará pelo NSU zero. Confirma?"))return;
  setCaptureBusy(true);setError("");setMessage("");setCaptureResult(null);
  try{
    const result=await invoke("/api/capture/download",{companyId,previousConsumerStopped:true});
    setCaptureResult(result);
    if(result.status===656)setError("SEFAZ 656 — consumo indevido. Aguarde pelo menos 1 hora antes de nova tentativa. "+result.reason);
    else if(result.status===137)setMessage("");
    else setMessage(`SEFAZ 138 — ${result.downloaded} XML(s) gravado(s) no HANA. Último NSU: ${String(result.lastNsu).padStart(15,"0")}.`);
    await refreshCapture();
  }catch(e){setError(e.message);await refreshCapture();}
  finally{setCaptureBusy(false);}
 }
 const current=steps[stage];
 return <div className="min-h-screen bg-[#f5f7fb] text-[#13223d]">
  <div className="flex min-h-screen">
   <aside className="hidden lg:flex w-[278px] flex-col bg-[#101d35] text-white shrink-0">
    <div className="flex items-center gap-3 px-7 h-[86px] border-b border-white/10">
      <div className="rounded-xl bg-blue-500/20 p-2.5 text-blue-300"><FileText size={23}/></div>
      <div><div className="font-bold tracking-tight text-[21px]">NexFiscal</div><div className="text-[10px] uppercase tracking-[0.17em] text-slate-400">Plataforma fiscal</div></div>
    </div>
    <div className="px-5 pt-10"><div className="px-3 pb-4 text-[11px] font-semibold tracking-widest text-slate-500">CONFIGURAÇÃO INICIAL</div>
      <nav className="space-y-2">{steps.map((s,i)=><button key={s.number} onClick={()=>navigate(i)} disabled={i===1&&!hanaReady||i===2&&!registered}
        className={`w-full flex items-center gap-3 rounded-xl px-3 py-3.5 text-left transition ${stage===i?"bg-blue-600 shadow-lg shadow-blue-950/30":"hover:bg-white/5"} disabled:opacity-40 disabled:cursor-not-allowed`}>
        <span className={`flex h-9 w-9 items-center justify-center rounded-lg ${stage===i?"bg-white/15":"bg-white/5"}`}><s.icon size={19}/></span>
        <span className="flex-1"><span className="block text-[13px] font-semibold">{s.label}</span><span className="block text-[11px] mt-0.5 text-slate-300/70">{s.helper}</span></span>
        {(i===0&&hanaReady||i===1&&registered)?<CheckCircle2 size={16} className="text-emerald-400"/>:i>stage?<LockKeyhole size={14} className="opacity-50"/>:<ChevronRight size={16} className="text-white/40"/>}
      </button>)}</nav>
    </div>
    <div className="mt-auto p-6"><div className="rounded-xl border border-white/10 bg-white/5 p-4"><div className="flex items-center gap-2 text-xs font-semibold"><ShieldCheck size={17} className="text-emerald-300"/> Ambiente local protegido</div><p className="mt-2 text-[11px] leading-5 text-slate-400">Configuração disponível somente neste computador. Dados fiscais e credenciais requerem acesso autorizado.</p></div><p className="text-xs text-slate-500 mt-5">NexFiscal · Versão de configuração</p></div>
   </aside>
   <div className="min-w-0 flex-1">
    <header className="flex min-h-[86px] items-center justify-between border-b border-slate-200 bg-white px-6 lg:px-12">
     <div className="flex items-center gap-3"><LayoutDashboard size={19} className="text-slate-400"/><span className="text-sm text-slate-500">Configuração</span><ChevronRight size={16} className="text-slate-300"/><span className="text-sm font-semibold">{current.label}</span></div>
     <span className="hidden sm:inline-flex items-center gap-2 rounded-full border border-slate-200 px-3 py-1.5 text-xs text-slate-600"><CircleDot size={13} className={hanaReady?"text-emerald-500":"text-amber-500"}/>{hanaReady?"Infraestrutura configurada":"Instalação em andamento"}</span>
    </header>
    <main className="mx-auto max-w-[1160px] px-5 py-10 sm:px-9 lg:px-12 lg:py-12">
     <div className="flex flex-wrap gap-2 mb-8 lg:hidden">{steps.map((s,i)=><button key={i} disabled={i===1&&!hanaReady||i===2&&!registered} onClick={()=>navigate(i)} className={`rounded-full px-4 py-2 text-xs font-semibold disabled:opacity-40 ${stage===i?"bg-blue-600 text-white":"bg-white text-slate-600 border border-slate-200"}`}>{s.number} {s.label}</button>)}</div>
     <div className="mb-9"><div className="flex items-center gap-2 text-xs font-bold uppercase tracking-[0.15em] text-blue-600">ETAPA {current.number} DE 03 <span className="h-1 w-1 rounded-full bg-blue-400"/> NEXFISCAL</div>
      <h1 className="mt-3 text-3xl font-bold tracking-tight sm:text-[35px]">{stage===0?"Conecte seu SAP HANA":stage===1?"Cadastre sua empresa":"Central de operação"}</h1>
      <p className="mt-2 text-sm leading-6 text-slate-500 max-w-[690px]">{stage===0?"Configure o banco de dados que armazenará os documentos e o controle fiscal. Essa configuração é realizada uma única vez.":stage===1?"Utilize o certificado digital A1 para identificar o contribuinte e registrar os dados no banco HANA.":"Acompanhe o estado da configuração e a preparação do motor de captura."}</p>
     </div>
     {error&&<div className="mb-5"><Notice type="error">{error}</Notice></div>}
     {message&&<div className="mb-5"><Notice type="success">{message}</Notice></div>}
     {stage===0&&<div className="grid gap-6 xl:grid-cols-[minmax(0,1.6fr)_minmax(280px,0.85fr)]">
       <Card><CardHeader><div className="flex items-center gap-3"><div className="p-3 bg-blue-50 rounded-xl text-blue-600"><Server size={24}/></div><div><h2 className="text-lg font-bold">Dados de conexão</h2><p className="text-sm text-slate-500 mt-1">SAP HANA · Banco intermediário BRAI_NEX</p></div></div></CardHeader>
        <CardContent><div className="grid gap-5 sm:grid-cols-[1fr_145px]"><div><Label>Servidor / host</Label><Input placeholder="hana.empresa.local" autoComplete="off" value={hana.server} onChange={e=>setH("server",e.target.value)}/></div><div><Label>Porta SQL</Label><Input inputMode="numeric" placeholder="30015" value={hana.port} onChange={e=>setH("port",e.target.value)}/></div></div>
        <div className="mt-5 grid gap-5 sm:grid-cols-2"><div><Label>Usuário do banco</Label><Input placeholder="NEXFISCAL_APP" autoComplete="off" value={hana.username} onChange={e=>setH("username",e.target.value)}/></div>
         <div><Label>Senha do usuário</Label><div className="relative"><Input type={showPassword?"text":"password"} autoComplete="new-password" value={hana.password} onChange={e=>setH("password",e.target.value)} className="pr-12"/><button type="button" onClick={()=>setShowPassword(!showPassword)} className="absolute right-3 top-3 text-slate-400" aria-label="Mostrar senha">{showPassword?<EyeOff size={18}/>:<Eye size={18}/>}</button></div></div></div>
        <div className="mt-7 flex flex-wrap gap-3 border-t border-slate-100 pt-6">
         <Button disabled={busy||!hana.server||!hana.username||!hana.password} variant="outline" onClick={()=>checkHana(false)}><PlugZap size={17}/>{busy?"Conectando...":"Testar conexão"}</Button>
         {hanaTablesFound!==null&&!hanaChecked&&<Button disabled={busy} variant="outline" onClick={provisionHana}><Database size={17}/>Preparar estrutura automaticamente</Button>}
         <Button disabled={busy||!hanaChecked} onClick={()=>checkHana(true)}><ShieldCheck size={17}/>Salvar e continuar<ArrowRight size={17}/></Button>
        </div></CardContent>
       </Card>
       <div className="space-y-5"><Card><CardHeader><h2 className="font-bold text-[16px]">Validação da estrutura</h2></CardHeader><CardContent><div className="flex gap-3 rounded-xl bg-slate-50 p-4"><Database className="shrink-0 text-blue-600" size={20}/><div><p className="font-semibold text-sm">Schema BRAI_NEX</p><p className="text-xs text-slate-500 mt-1 leading-5">O instalador verifica as cinco tabelas, cria as ausentes quando autorizado e preserva os dados existentes.</p></div></div><div className="mt-5 flex items-center gap-2 text-sm font-medium">{hanaChecked?<CheckCircle2 size={17} className="text-emerald-500"/>:<CircleDot size={17} className="text-slate-400"/>}{hanaChecked?"Estrutura validada":hanaTablesFound!==null?`${hanaTablesFound} de 5 tabelas encontradas`:"Aguardando teste da conexão"}</div></CardContent></Card>
        <Notice>As credenciais serão guardadas protegidas no servidor, não no navegador. Mantenha o diretório da aplicação e suas chaves com acesso restrito.</Notice></div>
      </div>}
     {stage===1&&<div className="grid gap-6 xl:grid-cols-[minmax(0,1.6fr)_minmax(280px,0.85fr)]"><Card><CardHeader><div className="flex items-center gap-3"><div className="p-3 bg-blue-50 rounded-xl text-blue-600"><ShieldCheck size={24}/></div><div><h2 className="text-lg font-bold">Certificado digital A1</h2><p className="text-sm text-slate-500 mt-1">Identificação automática da empresa</p></div></div></CardHeader><CardContent>
       <div className="rounded-xl border-2 border-dashed border-slate-200 bg-slate-50 p-6 text-center"><CloudUpload className="mx-auto text-blue-500" size={30}/><p className="mt-3 font-semibold text-sm">{file?file.name:"Selecione um arquivo .pfx ou .p12"}</p><p className="text-xs text-slate-400 mt-1">O certificado será criptografado e armazenado no servidor após o cadastro.</p><label className="inline-flex mt-4 cursor-pointer items-center rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-100"><input type="file" accept=".pfx,.p12" className="sr-only" onChange={e=>{setFile(e.target.files?.[0]||null);setIdentity(null);setRegistered(null)}}/>Selecionar certificado</label></div>
       <div className="mt-5"><Label>Senha do certificado</Label><Input type="password" autoComplete="off" value={password} onChange={e=>{setPassword(e.target.value);setIdentity(null);setRegistered(null)}} placeholder="Digite a senha do A1"/></div>
       <div className="flex gap-3 mt-6"><Button disabled={busy||!file||!password} onClick={()=>inspect(false)}>{busy?<RefreshCw size={17}/>:<BadgeCheck size={17}/>}Validar certificado</Button></div>
       {identity&&<div className="mt-7 border-t border-slate-100 pt-6"><h3 className="flex gap-2 font-bold text-slate-800"><CheckCircle2 size={19} className="text-emerald-500"/>Dados identificados</h3><div className="mt-4">{details("Razão social",identity.legalName)}{details("CNPJ",formatCnpj(identity.cnpj))}{details("UF",identity.state||"Não informada no Subject")}{details("Validade",new Date(identity.validUntil).toLocaleDateString("pt-BR"))}{details("Autoridade certificadora",issuerDisplayName(identity.issuer))}</div>
        <div className="mt-5"><Notice>Validação de cadeia ICP-Brasil, revogação e autorização SEFAZ ainda pendentes. O cadastro permanecerá pausado.</Notice></div>
        <Button className="mt-6" disabled={busy||!hanaReady} onClick={()=>inspect(true)}>Cadastrar empresa<ArrowRight size={17}/></Button>
       </div>}
      </CardContent></Card><div className="space-y-5"><Card><CardHeader><h2 className="font-bold">Infraestrutura conectada</h2></CardHeader><CardContent><div className="flex items-center gap-2 text-sm"><CheckCircle2 size={18} className="text-emerald-500"/>Banco SAP HANA configurado</div><div className="mt-4 flex items-center gap-2 text-sm text-slate-500"><ShieldCheck size={18}/>Controle interno de NSU</div><p className="mt-3 text-xs text-slate-500 leading-5">O NSU não é solicitado ao usuário. A captura começa somente após uma inicialização fiscal segura.</p></CardContent></Card></div></div>}
     {stage===2&&<div className="space-y-6">
       <div className="flex flex-wrap items-center justify-between gap-4">
         <div><h2 className="text-lg font-bold">Distribuição de NF-e</h2><p className="mt-1 text-sm text-slate-500">Verificação do ambiente antes da primeira consulta real à SEFAZ Nacional.</p></div>
         <Button variant="outline" onClick={refreshCapture} disabled={captureBusy}><RefreshCw size={17}/>Atualizar situação</Button>
       </div>
       {!captureState&&<Notice>Consulte a situação do HANA para verificar as empresas cadastradas.</Notice>}
       {captureState?.companies?.length===0&&<Notice>Não há empresa cadastrada. Volte à etapa de certificado para iniciar o cadastro.</Notice>}
       {captureState?.companies?.map(company=><Card key={company.companyId}>
         <CardHeader><div className="flex flex-wrap justify-between gap-3">
           <div><h3 className="font-bold text-lg">{company.legalName}</h3><p className="text-sm text-slate-500 mt-1">CNPJ {formatCnpj(company.cnpj)}</p></div>
           <span className={`rounded-full px-3 py-1.5 text-xs font-semibold ${company.ready?"bg-emerald-50 text-emerald-700":"bg-amber-50 text-amber-800"}`}>{company.ready?"Apto para teste controlado":"Consulta bloqueada"}</span>
         </div></CardHeader>
         <CardContent>
           <div className="grid gap-4 sm:grid-cols-3">
             <div className="rounded-lg bg-slate-50 p-4"><p className="text-xs text-slate-500">XMLs gravados no HANA</p><p className="mt-2 text-2xl font-bold">{company.documentsStored}</p></div>
             <div className="rounded-lg bg-slate-50 p-4"><p className="text-xs text-slate-500">Última tentativa registrada</p><p className="mt-2 text-sm font-semibold">{company.lastConsultationUtc?new Date(company.lastConsultationUtc).toLocaleString("pt-BR"):"Nenhuma"}</p></div>
             <div className="rounded-lg bg-slate-50 p-4"><p className="text-xs text-slate-500">Certificado A1</p><p className="mt-2 text-sm font-semibold"><span className={company.certificateStored?"text-emerald-700":"text-amber-800"}>{company.certificateStored?"Protegido no servidor":"Não encontrado"}</span></p></div>
           </div>
           <div className="mt-5 space-y-3 text-sm">
             <div className="flex gap-2 items-center">{company.cursorConfigured?<CheckCircle2 className="text-emerald-600" size={18}/>:<CircleDot className="text-amber-600" size={18}/>} Controle de NSU {company.cursorConfigured?"inicializado":"será inicializado na primeira consulta"}</div>
             <div className="flex gap-2 items-center">{company.enabled?<CheckCircle2 className="text-emerald-600" size={18}/>:<CircleDot className="text-amber-600" size={18}/>} Captura {company.enabled?"habilitada para execução manual":"ainda não iniciada"}</div>
           </div>
           <div className="mt-5"><Notice>{company.reason}</Notice></div>
           {connectivity[company.companyId]&&<div className="mt-5"><Notice type={connectivity[company.companyId].reachable?"success":"error"}>{connectivity[company.companyId].message}<div className="mt-1 text-xs font-medium">Etapa: {connectivity[company.companyId].stage || "desconhecida"} · Código: {connectivity[company.companyId].code || "indisponível"}{connectivity[company.companyId].httpStatus ? ` · HTTP ${connectivity[company.companyId].httpStatus}` : ""}{connectivity[company.companyId].hresult ? ` · ${connectivity[company.companyId].hresult}` : ""}</div>{connectivity[company.companyId].chainErrors?.length > 0 && <div className="mt-1 text-xs">Cadeia ICP-Brasil: {connectivity[company.companyId].chainErrors.join(", ")}</div>}{connectivity[company.companyId].causes?.length > 0 && <div className="mt-1 text-xs">Causas técnicas: {connectivity[company.companyId].causes.join(" → ")} · Protocolo: {connectivity[company.companyId].tlsPolicy || "padrão do sistema"}</div>}</Notice></div>}
           <div className="mt-5 flex flex-wrap items-center gap-3">
             <Button variant="outline" disabled={!company.certificateStored||captureBusy} onClick={()=>testConnectivity(company.companyId)}><PlugZap size={17}/>Testar conexão SEFAZ</Button>
             <Button disabled={!company.certificateStored||captureBusy||(company.nextAllowedUtc&&new Date(company.nextAllowedUtc)>new Date())} onClick={()=>downloadNfe(company.companyId)}>
               {captureBusy?<RefreshCw size={17}/>:<CloudUpload size={17}/>}
               {captureBusy?"Consultando SEFAZ...":"Baixar NF-e agora"}
             </Button>
             <span className="text-xs text-slate-500">Consulta manual controlada, sem agendamento automático</span>
           </div>
         </CardContent>
       </Card>)}
       {captureResult&&<Notice type={captureResult.status===656?"error":captureResult.status===137?"warning":"success"}>SEFAZ: {captureResult.status} · {captureResult.reason} · XMLs novos: {captureResult.downloaded}{captureResult.status===137?" · Aguarde pelo menos 1 hora antes de outra consulta.":""}</Notice>}
       <Notice>A primeira consulta inicializa automaticamente o NSU se não houver checkpoint. Execute somente após desligar o consumidor anterior; o sistema impede consultas repetidas durante o intervalo de segurança.</Notice>
     </div>}
     <div className="mt-10 text-xs text-slate-400">NexFiscal · Microsoft .NET 8 + React + Tailwind CSS · Configuração local</div>
    </main>
   </div>
  </div>
 </div>;
}
createRoot(document.getElementById("root")).render(<App/>);
