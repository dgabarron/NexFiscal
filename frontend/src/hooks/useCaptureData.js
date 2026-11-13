import { useCallback, useEffect, useState } from "react";
import { api } from "../lib/api";
import { mergeSchedule } from "../lib/capture-utils";

export function useCaptureData() {
  const [companies, setCompanies] = useState([]);
  const [cteStates, setCteStates] = useState({});
  const [schedules, setSchedules] = useState({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  const refresh = useCallback(async () => {
    setError("");
    try {
      const capture = await api.get("/api/capture/status");
      const list = capture.companies || [];
      setCompanies(list);
      const [cteEntries, scheduleEntries] = await Promise.all([
        Promise.all(list.map((c) => api.get(`/api/cte/status/${c.companyId}`).catch(() => null))),
        Promise.all(list.map((c) => api.get(`/api/capture/schedules/${c.companyId}`))),
      ]);
      const nextCte = {}, nextSchedules = {};
      list.forEach((c, i) => {
        if (cteEntries[i]) nextCte[c.companyId] = cteEntries[i];
        nextSchedules[c.companyId] = mergeSchedule(scheduleEntries[i]);
      });
      setCteStates(nextCte);
      setSchedules(nextSchedules);
    } catch (e) {
      setError(e.message);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const setScheduleField = useCallback((companyId, documentType, field, value) => {
    setSchedules((prev) => {
      const list = prev[companyId];
      if (!list) return prev;
      return {
        ...prev,
        [companyId]: list.map((s) =>
          s.documentType === documentType ? { ...s, [field]: value } : s
        ),
      };
    });
  }, []);

  const replaceSchedule = useCallback((companyId, list) => {
    setSchedules((prev) => ({ ...prev, [companyId]: mergeSchedule(list) }));
  }, []);

  return { loading, companies, cteStates, schedules, error, refresh, setScheduleField, replaceSchedule };
}
