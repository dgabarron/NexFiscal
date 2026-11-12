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
 const [cteResults,setCteResults]=useState({});
 const [cteStates,setCteStates]=useState({});
 const [scheduleSettings,setScheduleSettings]=useState({});
 const [scheduleSaving,setScheduleSaving]=useState({});
 const [scheduleNotice,setScheduleNotice]=useState({});
 const [recoveryDates,setRecoveryDates]=useState({});
 const [recoveryPreviews,setRecoveryPreviews]=useState({});
 const [restoreResults,setRestoreResults]=useState({});
 const [recoveryBusy,setRecoveryBusy]=useState({});
 const [historicalDates,setHistoricalDates]=useState({});
 const [historicalFolders,setHistoricalFolders]=useState({});
 const [historicalJobs,setHistoricalJobs]=useState({});
 const [historicalStarting,setHistoricalStarting]=useState({});
 const [connectivity,setConnectivity]=useState({});
 const [clockNow,setClockNow]=useState(Date.now());
 useEffect(()=>{
  const timer=setInterval(()=>setClockNow(Date.now()),15000);
  return ()=>clearInterval(timer);
 },[]);
 function pendingWindow(company){
  const date=company.nextAllowedUtc;
  if(!date)return null;
  // O HANA armazena UTC. Alguns drivers serializam TIMESTAMP sem sufixo Z.
  const canonical=/[zZ]$|[+-]\d{2}:\d{2}$/.test(date)?date:date+"Z";
  const ms=Date.parse(canonical);
  return Number.isFinite(ms)&&ms>clockNow
    ?{time:new Date(ms).toLocaleTimeString("pt-BR",{hour:"2-digit",minute:"2-digit"}),minutes:Math.ceil((ms-clockNow)/60000)}
    :null;
 }
 useEffect(()=>{fetch("/api/health").then(r=>r.json()).then(async x=>{
    if(!x.hanaConfigured)return;
    setHanaReady(true);
    try{
      const resp=await fetch("/api/capture/status");
      if(resp.ok){
        const state=await resp.json();
        setCaptureState(state);
        for(const company of state.companies||[])fetch(`/api/cte/status/${company.companyId}`).then(r=>r.ok?r.json():null).then(data=>{if(data)setCteStates(prev=>({...prev,[company.companyId]:data}));}).catch(()=>{});
        for(const company of state.companies||[])fetch(`/api/capture/schedules/${company.companyId}`).then(r=>r.ok?r.json():null).then(data=>{if(data)setScheduleSettings(prev=>({...prev,[company.companyId]:data.map(s=>({...s,intervalMinutes:String(s.intervalMinutes)}))}));}).catch(()=>{});
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
    const state=await resp.json();
    setCaptureState(state);
    for(const company of state.companies||[])fetch(`/api/cte/status/${company.companyId}`).then(r=>r.ok?r.json():null).then(data=>{if(data)setCteStates(prev=>({...prev,[company.companyId]:data}));}).catch(()=>{});
        for(const company of state.companies||[])fetch(`/api/capture/schedules/${company.companyId}`).then(r=>r.ok?r.json():null).then(data=>{if(data)setScheduleSettings(prev=>({...prev,[company.companyId]:data.map(s=>({...s,intervalMinutes:String(s.intervalMinutes)}))}));}).catch(()=>{});
  }catch(e){setError(e.message);}
 }
 async function testConnectivity(companyId){
  setCaptureBusy(true);setError("");setMessage("");
  try{
    const result=await invoke("/api/capture/test-connectivity",{companyId});
    setConnectivity(x=>({...x,[companyId]:result}));
  }catch(e){setError(e.message);}finally{setCaptureBusy(false);}
 }
 async function downloadNfe(company){
  const cooldown=pendingWindow(company);
  if(cooldown){setError("");setMessage(`Consulta temporariamente indisponível. Próxima tentativa após ${cooldown.time} (aproximadamente ${cooldown.minutes} min).`);return;}
  const companyId=company.companyId;
  const nfeSchedule=(scheduleSettings[companyId]||[]).find(s=>s.documentType==="NFE");
  if(!nfeSchedule?.enabled||!nfeSchedule.destinationDirectory?.trim()){
    setError("Ative a captura NF-e, informe a pasta de XMLs e salve a configuração.");return;
  }
  if(!window.confirm("O sistema anterior de captura NF-e está desligado para este CNPJ? Esta ação enviará uma consulta REAL à SEFAZ. Se não existir checkpoint, o NexFiscal iniciará pelo NSU zero. Confirma?"))return;
  setCaptureBusy(true);setError("");setMessage("");setCaptureResult(null);
  try{
    const result=await invoke("/api/capture/download",{companyId,previousConsumerStopped:true});
    setCaptureResult(result);
    if(result.saveSucceeded===false)setError(result.fileSaveError||"Falha ao salvar os arquivos XML.");
    else if(result.status===656)setError("SEFAZ 656 — consumo indevido. Aguarde pelo menos 1 hora antes de nova tentativa. "+result.reason);
    else if(result.status===137)setMessage("");
    else if(result.status===138&&result.filesSaved===0&&result.filesAlreadyPresent===0)
      setMessage("A SEFAZ retornou documentos, mas nenhum procNFe completo. Resumos e eventos não foram gravados na pasta. A NF-e completa pode depender da manifestação do destinatário.");
    else setMessage(`SEFAZ 138 — ${result.filesSaved} procNFe gravado(s) em ${result.destinationDirectory}; ${result.filesAlreadyPresent} já existente(s). ${result.skippedNonProcNFe||0} resumo(s)/evento(s) ignorado(s).`);
    await refreshCapture();
  }catch(e){setError(e.message);await refreshCapture();}
  finally{setCaptureBusy(false);}
 }
 function setScheduleField(companyId,type,field,value){
  setScheduleSettings(prev=>({...prev,[companyId]:(prev[companyId]||["NFE","CTE","NFSE"].map(documentType=>({documentType,enabled:false,intervalMinutes:"60",destinationDirectory:""}))).map(x=>x.documentType===type?{...x,[field]:value}:x)}));
  setScheduleNotice(prev=>({...prev,[companyId]:""}));
 }
 async function saveSchedule(companyId){
  const entries=scheduleSettings[companyId]||[];
  if(entries.length!==3){setError("Não foi possível carregar a configuração do agendamento.");return;}
  for(const s of entries){
   const minutes=String(s.intervalMinutes||"").trim();
   if(!/^[0-9]+$/.test(minutes)||BigInt(minutes)<1n||BigInt(minutes)>9223372036854775807n){
    setError("Informe um número inteiro de minutos a partir de 1, para cada tipo de documento.");return;
   }
   if(s.enabled&&!String(s.destinationDirectory||"").trim()){
    setError("Preencha a pasta de destino para todos os tipos com Captura ativa.");return;
   }
  }
  setScheduleSaving(prev=>({...prev,[companyId]:true}));setError("");
  try{
    const resp=await invoke(`/api/capture/schedules/${companyId}`,entries.map(s=>({...s,intervalMinutes:String(s.intervalMinutes).trim()})));
    setScheduleSettings(prev=>({...prev,[companyId]:resp.map(s=>({...s,intervalMinutes:String(s.intervalMinutes)}))}));
    setScheduleNotice(prev=>({...prev,[companyId]:"Configuração salva. O serviço automático ainda não está instalado; a captura não iniciará até essa etapa."}));
  }catch(e){setError(e.message);}
  finally{setScheduleSaving(prev=>({...prev,[companyId]:false}));}
 }
 function defaultRecoveryDate(){
   const date=new Date(Date.now()-89*86400000);
   return new Intl.DateTimeFormat("pt-BR",{day:"2-digit",month:"2-digit",year:"numeric",timeZone:"UTC"}).format(date);
 }
 function recoveryFrom(companyId){
   const chosen=(recoveryDates[companyId]||defaultRecoveryDate()).trim();
   const match=/^(\d{2})\/(\d{2})\/(\d{4})$/.exec(chosen);
   if(!match)throw new Error("Informe a data no formato DD/MM/AAAA.");
   const day=Number(match[1]),month=Number(match[2]),year=Number(match[3]);
   const date=new Date(Date.UTC(year,month-1,day));
   if(date.getUTCFullYear()!==year||date.getUTCMonth()!==month-1||date.getUTCDate()!==day)
     throw new Error("Data inválida. Use DD/MM/AAAA.");
   const now=Date.now(),from=date.getTime();
   if(from>now||from<Date.now()-90*86400000)
     throw new Error("Escolha uma data dentro dos últimos 90 dias.");
   return date.toISOString();
 }
 async function previewNfeRecovery(companyId){
   setRecoveryBusy(prev=>({...prev,[companyId]:true}));setError("");
   try{
     const from=recoveryFrom(companyId);
     const resp=await fetch(`/api/recovery/nfe/preview/${companyId}?from=${encodeURIComponent(from)}`);
     const data=await resp.json().catch(()=>({}));
     if(!resp.ok)throw new Error(data.error||data.title||"Não foi possível identificar NSUs antigos.");
     setRecoveryPreviews(prev=>({...prev,[companyId]:data}));
   }catch(e){setError(e.message);}
   finally{setRecoveryBusy(prev=>({...prev,[companyId]:false}));}
 }
 async function restoreNfeFromZip(companyId){
   const preview=recoveryPreviews[companyId];
   if(!preview?.scanComplete||preview.invalidFiles>0){
     setError("Antes de restaurar, resolva os arquivos inválidos ou a conferência incompleta.");return;
   }
   if(!window.confirm("Restaurar TODOS os XMLs completos encontrados SOMENTE nos ZIPs internos para a pasta configurada? Arquivos existentes não serão substituídos. Não haverá consulta à SEFAZ."))return;
   setRecoveryBusy(prev=>({...prev,[companyId]:true}));setError("");
   try{
     const result=await invoke("/api/recovery/nfe/restore",{companyId,from:recoveryFrom(companyId)});
     setRestoreResults(prev=>({...prev,[companyId]:result}));
     const resp=await fetch(`/api/recovery/nfe/preview/${companyId}?from=${encodeURIComponent(recoveryFrom(companyId))}`);
     if(resp.ok){const previewData=await resp.json();setRecoveryPreviews(prev=>({...prev,[companyId]:previewData}));}
   }catch(e){setError(e.message);}
   finally{setRecoveryBusy(prev=>({...prev,[companyId]:false}));}
 }
 async function startHistorical(companyId){
   const fromDate=(historicalDates[companyId]||"01/01/1900").trim();
   if(!/^\d{2}\/\d{2}\/\d{4}$/.test(fromDate)){setError("Data inicial inválida. Informe DD/MM/AAAA.");return;}
   const sourceDirectories=(historicalFolders[companyId]||"").split("\n").map(x=>x.trim()).filter(Boolean);
   if(!window.confirm("Iniciar recuperação dos XMLs completos em pastas históricas, ZIPs e pasta atual? O sistema não consulta notas antigas indisponíveis na SEFAZ. Arquivos existentes não serão sobrescritos."))return;
   setHistoricalStarting(prev=>({...prev,[companyId]:true}));setError("");
   try{
     const result=await invoke("/api/history/nfe/start",{companyId,fromDate,sourceDirectories});
     setHistoricalJobs(prev=>({...prev,[companyId]:result}));
   }catch(e){setError(e.message);}
   finally{setHistoricalStarting(prev=>({...prev,[companyId]:false}));}
 }
 useEffect(()=>{
   const timer=setInterval(()=>{
     Object.entries(historicalJobs).forEach(([companyId,job])=>{
       if(job?.status!=="running")return;
       fetch(`/api/history/nfe/status/${companyId}`)
         .then(resp=>resp.ok?resp.json():null)
         .then(data=>{if(data)setHistoricalJobs(prev=>({...prev,[companyId]:data}));})
         .catch(()=>{});
     });
   },2500);
   return ()=>clearInterval(timer);
 },[historicalJobs]);
 async function downloadCte(company){
  const id=company.companyId;
  const cteSchedule=(scheduleSettings[id]||[]).find(s=>s.documentType==="CTE");
  if(!cteSchedule?.enabled||!cteSchedule.destinationDirectory?.trim()){
    setError("Ative a captura CT-e, informe a pasta de XMLs e salve a configuração.");return;
  }
  const cooldown=pendingWindow(cteStates[id]||{});
  if(cooldown){setError("");setMessage(`CT-e: próxima consulta após ${cooldown.time} (${cooldown.minutes} min).`);return;}
  if(!window.confirm("Confirma que não existe outro capturador CT-e ativo para este CNPJ? A consulta será enviada à SEFAZ."))return;
  setCaptureBusy(true);setError("");setMessage("");
  try{
    const result=await invoke("/api/cte/download",{companyId:id,previousConsumerStopped:true});
    setCteResults(prev=>({...prev,[id]:result}));
    if(result.saveSucceeded===false)setError(result.message||"Não foi possível concluir a captura CT-e.");
    else if(result.status===137)setMessage("CT-e: nenhum documento disponível.");
    else setMessage(`CT-e: ${result.filesSaved} XML(s) completos gravados na pasta X; ${result.skippedNonProcCTe} outros retornos ignorados.`);
    const resp=await fetch(`/api/cte/status/${id}`);
    if(resp.ok){const state=await resp.json();setCteStates(prev=>({...prev,[id]:state}));}
  }catch(e){setError(e.message);}finally{setCaptureBusy(false);}
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
     {stage===2&&<div className="space-y-5">
       <div className="flex flex-wrap items-center justify-between gap-4">
         <div><h2 className="text-lg font-bold">Captura de NF-e e CT-e</h2><p className="mt-1 text-sm text-slate-500">Consulte a SEFAZ e salve somente XMLs completos de NF-e (procNFe) e CT-e (procCTe) na pasta escolhida.</p></div>
         <Button variant="outline" disabled={captureBusy} onClick={refreshCapture}><RefreshCw size={17}/>Atualizar dados</Button>
       </div>
       {!captureState&&<Notice>Não foi possível carregar os dados da operação. Clique em Atualizar dados.</Notice>}
       {captureState?.companies?.length===0&&<Notice>Não há empresa cadastrada para consultar.</Notice>}
       {captureState?.companies?.map(company=>{
         const cooldown=pendingWindow(company);
         const connected=connectivity[company.companyId];
         return <Card key={company.companyId}>
           <CardHeader>
             <div className="flex flex-wrap items-center justify-between gap-3">
               <div><h3 className="text-lg font-bold">{company.legalName}</h3><p className="mt-1 text-sm text-slate-500">CNPJ {formatCnpj(company.cnpj)}</p></div>
               <span className={`rounded-full px-3 py-1.5 text-xs font-semibold ${!company.certificateStored?"bg-amber-50 text-amber-800":cooldown?"bg-amber-50 text-amber-800":"bg-emerald-50 text-emerald-800"}`}>
                 {!company.certificateStored?"Certificado ausente":cooldown?"Aguardando próxima consulta":"Consulta disponível"}
               </span>
             </div>
           </CardHeader>
           <CardContent>
             <div className="grid gap-3 sm:grid-cols-3">
               <div className="rounded-lg bg-slate-50 p-4">
                 <p className="text-xs text-slate-500">XMLs armazenados</p>
                 <p className="mt-2 text-2xl font-bold">{company.documentsStored}</p>
               </div>
               <div className="rounded-lg bg-slate-50 p-4">
                 <p className="text-xs text-slate-500">Última tentativa</p>
                 <p className="mt-2 text-sm font-semibold">{company.lastConsultationUtc?new Date(company.lastConsultationUtc.endsWith("Z")?company.lastConsultationUtc:company.lastConsultationUtc+"Z").toLocaleString("pt-BR"):"Ainda não realizada"}</p>
               </div>
               <div className="rounded-lg bg-slate-50 p-4">
                 <p className="text-xs text-slate-500">Próxima consulta</p>
                 <p className={`mt-2 text-sm font-semibold ${cooldown?"text-amber-800":"text-emerald-800"}`}>{cooldown?`Após ${cooldown.time} (${cooldown.minutes} min)`:company.certificateStored?"Disponível agora":"Aguardando certificado"}</p>
               </div>
             </div>
             <div className="mt-4 rounded-lg border border-slate-200 p-4">
               <h4 className="font-semibold text-sm text-slate-800">Controle de NSU (consulta local, sem acessar a SEFAZ)</h4>
               <div className="mt-3 grid gap-3 sm:grid-cols-2">
                 <div>
                   <p className="text-xs text-slate-500">NF-e · Último NSU persistido</p>
                   <p className="font-mono text-sm font-semibold">{company.lastNsu||"Não inicializado"}</p>
                   <p className="mt-1 text-xs text-slate-500">Maior NSU informado: {company.maxNsu||"Ainda não armazenado"}</p>
                 </div>
                 <div>
                   <p className="text-xs text-slate-500">CT-e · Último NSU persistido</p>
                   <p className="font-mono text-sm font-semibold">{cteStates[company.companyId]?.lastNsu||"Não inicializado"}</p>
                   <p className="mt-1 text-xs text-slate-500">Maior NSU informado: {cteStates[company.companyId]?.maxNsu||"Ainda não informado"}</p>
                 </div>
               </div>
               <p className="mt-3 text-xs text-slate-500">Estes valores não consultam a SEFAZ. O NSU da NF-e e o do CT-e são sequências independentes. Não altere os valores manualmente.</p>
             </div>
             {!company.certificateStored&&<div className="mt-4"><Notice>O certificado digital A1 não está disponível no servidor.</Notice></div>}
             {cooldown&&<div className="mt-4"><Notice>A última tentativa foi registrada. O sistema aguardará o horário permitido antes de enviar outra consulta à SEFAZ. Não é necessário alterar NSU ou banco.</Notice></div>}
             {connected&&<div className="mt-4"><Notice type={connected.reachable?"success":"error"}>
               <div className="font-semibold">{connected.reachable?"Conexão HTTPS com a SEFAZ confirmada":connected.message}</div>
               {!connected.reachable&&<div className="mt-1 text-xs">Etapa: {connected.stage||"não identificada"} · Código: {connected.code||"indisponível"}{connected.causes?.length>0?` · ${connected.causes.join(" → ")}`:""}</div>}
             </Notice></div>}
             <div className="mt-5 rounded-lg border border-slate-200 p-4 space-y-3">
               <h4 className="font-semibold text-sm">Configuração de captura por tipo</h4>
               <p className="text-xs text-slate-500">Intervalo livre em minutos (mínimo 1). A frequência efetiva respeitará o intervalo obrigatório de cada serviço fiscal. Salve as configurações antes da consulta manual. O serviço automático ainda será implementado.</p>
               {(scheduleSettings[company.companyId]||[]).map(s=><div key={s.documentType} className="rounded-lg bg-slate-50 p-3">
                 <div className="flex flex-wrap items-center justify-between gap-3">
                   <label className="flex items-center gap-2 text-sm font-semibold"><input type="checkbox" checked={s.enabled} onChange={e=>setScheduleField(company.companyId,s.documentType,"enabled",e.target.checked)}/>Captura ativa — {s.documentType==="NFE"?"NF-e":s.documentType==="CTE"?"CT-e":"NFS-e"}</label>
                   <div className="flex items-center gap-2">
                     <label className="text-xs text-slate-600">Intervalo (min)</label>
                     <input type="text" inputMode="numeric" pattern="[0-9]*" className="w-32 rounded-md border border-slate-300 bg-white p-2 text-sm" value={s.intervalMinutes} onChange={e=>setScheduleField(company.companyId,s.documentType,"intervalMinutes",e.target.value)} />
                   </div>
                 </div>
                 <div className="mt-3">
                   <label className="mb-1 block text-xs text-slate-600">Pasta dos XMLs</label>
                   <input type="text" className="w-full rounded-md border border-slate-300 p-2 text-sm disabled:bg-slate-100 disabled:text-slate-400" placeholder="Ex.: C:\\NexFiscal\\XML" value={s.destinationDirectory||""} disabled={!s.enabled} onChange={e=>setScheduleField(company.companyId,s.documentType,"destinationDirectory",e.target.value)}/>
                 </div>
               </div>)}
               <Button variant="outline" disabled={!scheduleSettings[company.companyId]||scheduleSaving[company.companyId]} onClick={()=>saveSchedule(company.companyId)}>{scheduleSaving[company.companyId]?"Salvando...":"Salvar configurações de captura"}</Button>
               {scheduleNotice[company.companyId]&&<p className="text-xs text-amber-800">{scheduleNotice[company.companyId]}</p>}
             </div>
             <div className="mt-5 rounded-lg border border-slate-200 p-4 space-y-3">
               <h4 className="font-semibold text-sm">Recuperação histórica de XMLs — NF-e</h4>
               <p className="text-xs text-slate-600">Procura automaticamente XMLs completos (procNFe) na pasta atual, em todos os ZIPs internos do NexFiscal e nas pastas históricas informadas. Busca desde a data escolhida até hoje, sem seleção de notas e sem duplicar por chave de acesso.</p>
               <div className="grid gap-3 md:grid-cols-2">
                 <div>
                   <label className="mb-1 block text-xs text-slate-600">Desde (DD/MM/AAAA)</label>
                   <input type="text" inputMode="numeric" maxLength={10} className="w-40 rounded-md border border-slate-300 p-2 text-sm" placeholder="DD/MM/AAAA" value={historicalDates[company.companyId]??"01/01/1900"}
                     onChange={e=>{const digits=e.target.value.replace(/\D/g,"").slice(0,8);const value=digits.length>4?digits.slice(0,2)+"/"+digits.slice(2,4)+"/"+digits.slice(4):digits.length>2?digits.slice(0,2)+"/"+digits.slice(2):digits;setHistoricalDates(prev=>({...prev,[company.companyId]:value}));}}/>
                 </div>
                 <div>
                   <label className="mb-1 block text-xs text-slate-600">Pastas históricas adicionais (uma por linha, opcionais)</label>
                   <textarea rows={3} className="w-full rounded-md border border-slate-300 p-2 text-sm" placeholder={"C:\\\\Historico\\\\NF-e\\nD:\\\\Backup\\\\XML"} value={historicalFolders[company.companyId]||""}
                     onChange={e=>setHistoricalFolders(prev=>({...prev,[company.companyId]:e.target.value}))}/>
                 </div>
               </div>
               <p className="text-xs text-amber-800">A recuperação histórica procura nas fontes de arquivos às quais o servidor tem acesso. Para notas nunca armazenadas e fora do período disponibilizado pela SEFAZ, é necessário fornecer outra fonte autorizada. O processo não reinicia o NSU e não faz busca fiscal por data.</p>
               <Button disabled={!company.certificateStored||!!historicalStarting[company.companyId]||historicalJobs[company.companyId]?.status==="running"}
                 onClick={()=>startHistorical(company.companyId)}>{historicalJobs[company.companyId]?.status==="running"?"Recuperação em andamento":historicalStarting[company.companyId]?"Iniciando...":"Recuperar todos os XMLs possíveis"}</Button>
               {historicalJobs[company.companyId]&&<div className="rounded-md bg-slate-50 p-3 space-y-2 text-sm">
                 <div className="font-semibold">Estado: {historicalJobs[company.companyId].status==="running"?"Em execução":historicalJobs[company.companyId].status==="completed"?"Concluído":historicalJobs[company.companyId].status==="failed"?"Falhou":"Interrompido"} · Examinados: {historicalJobs[company.companyId].examined}</div>
                 <div className="grid grid-cols-2 gap-2 md:grid-cols-4 text-xs">
                   <span>Novos XMLs: <strong>{historicalJobs[company.companyId].imported}</strong></span>
                   <span>Já existentes: <strong>{historicalJobs[company.companyId].existing}</strong></span>
                   <span>Conflitos: <strong>{historicalJobs[company.companyId].conflicts}</strong></span>
                   <span>Inválidos: <strong>{historicalJobs[company.companyId].invalid}</strong></span>
                   <span>Outras empresas: <strong>{historicalJobs[company.companyId].otherCompany}</strong></span>
                   <span>Fora do período: <strong>{historicalJobs[company.companyId].outsidePeriod}</strong></span>
                   <span>XMLs em ZIPs: <strong>{historicalJobs[company.companyId].xmlFromZip}</strong></span>
                   <span>XMLs em pastas: <strong>{historicalJobs[company.companyId].xmlFromFolder}</strong></span>
                 </div>
                 <p className="text-xs text-slate-600">{historicalJobs[company.companyId].message}</p>
                 {historicalJobs[company.companyId].scanComplete===false&&<p className="text-xs text-red-700">A varredura não foi completa. Confira o acesso às fontes ou os limites de segurança.</p>}
               </div>}
             </div>
             <div className="mt-5 flex flex-wrap gap-3">
               <Button disabled={!company.certificateStored||captureBusy} onClick={()=>downloadNfe(company)}>
                 {captureBusy?<RefreshCw size={17}/>:<CloudUpload size={17}/>}
                 {captureBusy?"Executando consulta...":cooldown?"Ver quando posso consultar":"Consultar SEFAZ e salvar XMLs"}
               </Button>
               <Button variant="outline" disabled={!company.certificateStored||captureBusy} onClick={()=>downloadCte(company)}>
                 {captureBusy?<RefreshCw size={17}/>:<CloudUpload size={17}/>}
                 {pendingWindow(cteStates[company.companyId]||{})?"Ver horário CT-e":"Consultar CT-e e salvar XMLs"}
               </Button>
               <Button variant="outline" disabled={!company.certificateStored||captureBusy} onClick={()=>testConnectivity(company.companyId)}><PlugZap size={17}/>Testar conexão</Button>
             </div>
             {cteStates[company.companyId]&&<div className="mt-4 text-xs text-slate-600">CT-e · Último NSU: {cteStates[company.companyId].lastNsu} · Próxima consulta: {pendingWindow(cteStates[company.companyId])?.time||"disponível"}</div>}
             {cteResults[company.companyId]&&<div className="mt-3"><Notice type={cteResults[company.companyId].saveSucceeded===false?"error":cteResults[company.companyId].status===137||(cteResults[company.companyId].filesSaved===0&&cteResults[company.companyId].filesAlreadyPresent===0)?"warning":"success"}>
               CT-e {cteResults[company.companyId].status}: {cteResults[company.companyId].reason} · procCTe novos: {cteResults[company.companyId].filesSaved||0} · Outros: {cteResults[company.companyId].skippedNonProcCTe||0}
               {cteResults[company.companyId].archiveUrl&&<div className="mt-2"><a href={cteResults[company.companyId].archiveUrl} className="underline">Baixar lote de recuperação CT-e</a></div>}
             </Notice></div>}
           </CardContent>
         </Card>;
       })}
       {captureResult&&<Notice type={captureResult.saveSucceeded===false||captureResult.status===656?"error":captureResult.status===137||(captureResult.status===138&&(captureResult.filesSaved??0)===0&&(captureResult.filesAlreadyPresent??0)===0)?"warning":"success"}>
         SEFAZ {captureResult.status}: {captureResult.reason} · procNFe novos: {captureResult.filesSaved??0} · Outros retornos: {captureResult.skippedNonProcNFe??0}
         {captureResult.fileSaveError&&<div className="mt-2 text-sm font-semibold">{captureResult.fileSaveError}</div>}
         {captureResult.skippedNonProcNFe>0&&<div className="mt-2 text-sm">A SEFAZ retornou resumos ou eventos, que não são XMLs completos. Pode ser necessária a manifestação do destinatário para receber procNFe.</div>}
         {captureResult.archiveUrl&&<div className="mt-3"><a href={captureResult.archiveUrl} className="inline-flex items-center gap-2 rounded-lg bg-emerald-700 px-4 py-2 text-sm font-semibold text-white hover:bg-emerald-800"><CloudUpload size={17}/>Baixar lote bruto para diagnóstico (.zip)</a></div>}
         {captureResult.persistenceWarning&&<div className="mt-3 text-sm text-amber-800">{captureResult.persistenceWarning}</div>}
       </Notice>}
     </div>}
     <div className="mt-10 text-xs text-slate-400">NexFiscal · Microsoft .NET 8 + React + Tailwind CSS · Configuração local</div>
    </main>
   </div>
  </div>
 </div>;
}
createRoot(document.getElementById("root")).render(<App/>);
