import { useCallback, useEffect, useRef, useState } from "react";
import type { LocalOrderPage } from "@laundry/api-client";
import { useTranslation } from "react-i18next";
import { loadLocalOrders, type LocalOrderLoader, type LocalOrderProblem } from "../gateway/local-orders";

export function LocalOrders({ load = loadLocalOrders, onSourceRequired }: { load?: LocalOrderLoader; onSourceRequired?: () => void }) {
  const { t, i18n } = useTranslation();
  const [snapshot, setSnapshot] = useState<LocalOrderPage | null>(null);
  const saved = useRef<LocalOrderPage | null>(null);
  const [problem, setProblem] = useState<LocalOrderProblem | null>(null);
  const [busy, setBusy] = useState(true);
  const pending = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const sourceRequired = useRef(onSourceRequired);
  sourceRequired.current = onSourceRequired;
  const request = useCallback(async (append = false) => {
    const before = saved.current;
    if (append && !before?.hasMore) return;
    pending.current?.abort();
    const controller = new AbortController(); pending.current = controller;
    const ticket = ++generation.current;
    setBusy(true); setProblem(null);
    try {
      const result = await load(append ? before!.nextCursor! : undefined, controller.signal);
      if (ticket !== generation.current || controller.signal.aborted) return;
      if (result.kind !== "ready") {
        setProblem(result.kind);
        if (["sourceRequired", "forbidden", "disabled"].includes(result.kind)) { saved.current = null; setSnapshot(null); }
        if (result.kind === "sourceRequired") sourceRequired.current?.();
        return;
      }
      const page = result.page;
      if (append && (page.lastSuccessfulPageAtUtc !== before!.lastSuccessfulPageAtUtc || page.items.some((item) => before!.items.some((old) => old.id === item.id)))) {
        setProblem("changed"); return;
      }
      const next = { ...page, items: append ? [...before!.items, ...page.items] : page.items };
      saved.current = next; setSnapshot(next);
    } catch {
      if (ticket === generation.current && !controller.signal.aborted) setProblem("unavailable");
    } finally { if (ticket === generation.current && !controller.signal.aborted) setBusy(false); }
  }, [load]);
  useEffect(() => {
    saved.current = null; setSnapshot(null);
    void request();
    return () => { generation.current++; pending.current?.abort(); };
  }, [request]);

  const locale = i18n.resolvedLanguage?.startsWith("sv") ? "sv-SE" : "en-GB";
  const date = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeZone: "UTC" });
  const time = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short", timeZone: "Europe/Stockholm" });
  const items = snapshot?.items ?? [];
  const notice = busy ? "loading" : problem === "unavailable" && items.length ? "retained" : problem ?? "readOnly";
  const danger = problem === "unavailable" && !busy;
  const warning = !busy && problem && !danger;
  return <section className="local-orders" aria-labelledby="local-orders-title">
    <div className="local-orders__header">
      <div><p className="simulator-label">{t("orders.context")}</p><h2 id="local-orders-title">{t("orders.title")}</h2></div>
      <button type="button" className="local-orders__button" onClick={() => void request()} disabled={busy}>
        {t(problem === "changed" ? "orders.restart" : "orders.refresh")}
      </button>
    </div>
    <p className="scan-boundary">{t("orders.description")}</p>
    <p className="local-orders__activity">
      {snapshot ? snapshot.lastSuccessfulPageAtUtc ? t("orders.activity", { time: time.format(new Date(snapshot.lastSuccessfulPageAtUtc)) }) : t("orders.noActivity") : t("orders.activityUnknown")}
    </p>
    <div className={`local-orders__notice${danger ? " local-orders__notice--danger" : warning ? " local-orders__notice--warning" : ""}`} role="status" aria-live="polite">
      <span aria-hidden="true">{danger || warning ? "!" : busy ? "…" : "i"}</span><p>{t(`orders.message.${notice}`)}</p>
    </div>
    <div className="local-orders__viewport" aria-busy={busy} tabIndex={0} role="region" aria-label={t("orders.listLabel")}>
      {items.length ? <ol className="local-orders__list" aria-label={t("orders.listLabel")}>{items.map((item) => <li key={item.id}>
        <dl>
          <div><dt>{t("orders.column.order")}</dt><dd className="local-orders__number">{item.orderNumber}</dd></div>
          <div><dt>{t("orders.column.customer")}</dt><dd>{item.customer.displayName}<span className="local-orders__customer-code">{item.customer.code}</span></dd></div>
          <div><dt>{t("orders.column.receiving")}</dt><dd><time dateTime={item.plannedReceivingDate}>{date.format(new Date(item.plannedReceivingDate + "T00:00:00Z"))}</time></dd></div>
          <div><dt>{t("orders.column.delivery")}</dt><dd><time dateTime={item.plannedDeliveryDate}>{date.format(new Date(item.plannedDeliveryDate + "T00:00:00Z"))}</time></dd></div>
          <div><dt>{t("orders.column.status")}</dt><dd>{t("orders.planned")}</dd></div>
        </dl>
      </li>)}</ol> : <p className="local-orders__empty">{busy ? t("orders.wait") : problem ? t("orders.notDisplayed") : t(snapshot?.lastSuccessfulPageAtUtc ? "orders.empty" : "orders.notDownloaded")}</p>}
    </div>
    <div className="local-orders__footer">
      <p>{t("orders.shown", { value: snapshot ? new Intl.NumberFormat(locale).format(items.length) : "—" })}</p>
      <button type="button" className="local-orders__button" disabled={busy || !snapshot?.hasMore || problem === "changed"} onClick={() => void request(true)}>{t("orders.more")}</button>
    </div>
  </section>;
}
