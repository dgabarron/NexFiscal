import { useEffect, useRef } from "react";
import { api } from "../lib/api";

export function useCatchupPolling(runs, setRuns) {
  const runsRef = useRef(runs);
  runsRef.current = runs;

  useEffect(() => {
    const timer = setInterval(async () => {
      const running = Object.entries(runsRef.current).filter(([, job]) => job?.status === "running");
      if (running.length === 0) return;
      const results = await Promise.all(
        running.map(([companyId]) =>
          api.get(`/api/sefaz/nfe/catchup/${companyId}`).catch(() => null)
        )
      );
      setRuns((prev) => {
        const next = { ...prev };
        running.forEach(([companyId], i) => { if (results[i]) next[companyId] = results[i]; });
        return next;
      });
    }, 3000);
    return () => clearInterval(timer);
  }, [setRuns]);
}
