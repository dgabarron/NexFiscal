import React, { useEffect, useRef, useState } from "react";
import { Badge, FluentProvider, Tab, TabList, Text, makeStyles, tokens, webDarkTheme, webLightTheme } from "@fluentui/react-components";
import { ArrowDownloadFilled, ArrowDownloadRegular, ClockArrowDownloadFilled, ClockArrowDownloadRegular, BuildingFilled, BuildingRegular, DatabaseFilled, DatabaseRegular, bundleIcon } from "@fluentui/react-icons";
import HanaPage from "./pages/HanaPage.jsx";
import CompaniesPage from "./pages/CompaniesPage.jsx";
import CapturePage from "./pages/CapturePage.jsx";
import MonitorPage from "./pages/MonitorPage.jsx";
import { describeStatus } from "./api.js";

const DatabaseIcon = bundleIcon(DatabaseFilled, DatabaseRegular);
const BuildingIcon = bundleIcon(BuildingFilled, BuildingRegular);
const CaptureIcon = bundleIcon(ArrowDownloadFilled, ArrowDownloadRegular);
const MonitorIcon = bundleIcon(ClockArrowDownloadFilled, ClockArrowDownloadRegular);
const BADGE_COLOR = { ok: "success", warn: "warning", error: "danger" };

const useStyles = makeStyles({
  provider: { minHeight: "100vh", display: "flex", flexDirection: "column", backgroundColor: tokens.colorNeutralBackground2 },
  header: {
    height: "48px", flexShrink: 0, display: "flex", alignItems: "center", gap: tokens.spacingHorizontalM,
    padding: `0 ${tokens.spacingHorizontalL}`, backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  mark: {
    width: "28px", height: "28px", borderRadius: tokens.borderRadiusMedium, display: "grid", placeItems: "center",
    backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand, fontSize: "18px",
  },
  product: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
  tagline: { color: tokens.colorNeutralForeground3 },
  spacer: { flexGrow: 1 },
  body: { flexGrow: 1, display: "flex", minHeight: 0, "@media (max-width: 760px)": { flexDirection: "column" } },
  nav: {
    width: "248px", flexShrink: 0, padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalS}`,
    "@media (max-width: 760px)": {
      width: "auto", padding: `0 ${tokens.spacingHorizontalS}`, backgroundColor: tokens.colorNeutralBackground1,
      borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, overflowX: "auto",
    },
  },
  tabLabel: { display: "inline-flex", alignItems: "center", gap: tokens.spacingHorizontalS },
  content: {
    flexGrow: 1, minWidth: 0, padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXXL} ${tokens.spacingVerticalXXXL}`,
    "@media (max-width: 760px)": { padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL} ${tokens.spacingVerticalXXL}` },
  },
  page: { maxWidth: "880px", display: "flex", flexDirection: "column", gap: tokens.spacingVerticalL },
});

function useMedia(query) {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const media = window.matchMedia(query);
    const listener = () => setMatches(media.matches);
    media.addEventListener("change", listener);
    return () => media.removeEventListener("change", listener);
  }, [query]);
  return matches;
}

export default function App() {
  const styles = useStyles();
  const dark = useMedia("(prefers-color-scheme: dark)");
  const narrow = useMedia("(max-width: 760px)");
  const [tab, setTab] = useState("hana");
  const [hana, setHana] = useState(undefined); // undefined = ainda carregando

  const chosen = useRef(false); // o operador já escolheu uma seção: não trocar sozinho

  // Ao abrir: se o HANA já está pronto, a tela útil é a de empresas — desde que o operador ainda não tenha escolhido outra.
  function onStatus(status) {
    setHana(previous => {
      if (previous === undefined && status?.state === "ready" && !chosen.current) setTab("companies");
      return status;
    });
  }
  function choose(value) { chosen.current = true; setTab(value); }

  const hanaView = describeStatus(hana);
  return <FluentProvider theme={dark ? webDarkTheme : webLightTheme} className={styles.provider}>
    <header className={styles.header}>
      <div className={styles.mark} aria-hidden="true"><DatabaseFilled/></div>
      <Text className={styles.product}>NexFiscal</Text>
      {!narrow && <Text size={200} className={styles.tagline}>Captura de NF-e para SAP Business One</Text>}
      <div className={styles.spacer}/>
      <Badge appearance="outline" color="informative">V1 · Etapa 4 de 4</Badge>
    </header>
    <div className={styles.body}>
      <nav className={styles.nav} aria-label="Seções">
        <TabList vertical={!narrow} size="large" selectedValue={tab} onTabSelect={(_, data) => choose(data.value)}>
          <Tab value="hana" icon={<DatabaseIcon/>}>
            <span className={styles.tabLabel}>Conexão SAP HANA
              {hana && BADGE_COLOR[hanaView.tone] && <Badge size="extra-small" color={BADGE_COLOR[hanaView.tone]} aria-label={hanaView.title}/>}
            </span>
          </Tab>
          <Tab value="companies" icon={<BuildingIcon/>}>Empresas</Tab>
          <Tab value="capture" icon={<CaptureIcon/>}>Captura de NF-e</Tab>
          <Tab value="monitor" icon={<MonitorIcon/>} aria-label="Monitoramento automático">Monitoramento</Tab>
        </TabList>
      </nav>
      <main className={styles.content}>
        {/* A seção HANA permanece montada para não perder o que foi digitado ao trocar de seção. */}
        <div hidden={tab !== "hana"} className={styles.page}><HanaPage onStatus={onStatus}/></div>
        {tab === "companies" && <div className={styles.page}><CompaniesPage hana={hana} goToHana={() => choose("hana")}/></div>}
        {tab === "capture" && <div className={styles.page}><CapturePage hana={hana} goToHana={() => choose("hana")} goToCompanies={() => choose("companies")}/></div>}
        {tab === "monitor" && <div className={styles.page}><MonitorPage hana={hana} goToHana={() => choose("hana")} goToCompanies={() => choose("companies")}/></div>}
      </main>
    </div>
  </FluentProvider>;
}
