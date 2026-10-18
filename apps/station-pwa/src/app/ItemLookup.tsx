import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { previewDevelopmentScan, type ScanPreview as Lookup, type ScanPreviewOutcome } from "../gateway/scan-preview";
import { groupPreviewOrders, plantDate } from "./order-preview-groups";

const fixtures = {
  towel: "SIMULATED-RFID-TOWEL-001", sheet: "SIMULATED-RFID-SHEET-001",
  mop: "SIMULATED-RFID-MOP-001", unknown: "SIMULATED-RFID-UNKNOWN",
} as const;
type Fixture = keyof typeof fixtures;

export function ItemLookup({ lookup = previewDevelopmentScan, onSourceRequired }: { lookup?: Lookup; onSourceRequired?: () => void }) {
  const { t, i18n } = useTranslation();
  const [choice, setChoice] = useState<Fixture>("towel");
  const [state, setState] = useState<ScanPreviewOutcome | { kind: "idle" | "loading" }>({ kind: "idle" });
  const [previewId, setPreviewId] = useState<string | null>(null);
  const [today, setToday] = useState(() => plantDate());
  useEffect(() => {
    const update = () => setToday(plantDate());
    const timer = setInterval(update, 60_000);
    window.addEventListener("focus", update);
    document.addEventListener("visibilitychange", update);
    return () => { clearInterval(timer); window.removeEventListener("focus", update); document.removeEventListener("visibilitychange", update); };
  }, []);
  const pending = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const sourceRequired = useRef(onSourceRequired); sourceRequired.current = onSourceRequired;
  useEffect(() => () => { generation.current++; pending.current?.abort(); }, [lookup]);
  const change = (value: Fixture) => {
    generation.current++; pending.current?.abort(); pending.current = null;
    setChoice(value); setState({ kind: "idle" }); setPreviewId(null);
  };
  const run = async () => {
    setToday(plantDate());
    pending.current?.abort(); const controller = new AbortController(); pending.current = controller;
    const ticket = ++generation.current; setState({ kind: "loading" }); setPreviewId(null);
    try {
      const result = await lookup(fixtures[choice], controller.signal);
      if (ticket !== generation.current || controller.signal.aborted) return;
      setState(result);
      if (result.kind === "recognized" && result.orders.length === 1) setPreviewId(result.orders[0]!.id);
      if (result.kind === "sourceRequired") sourceRequired.current?.();
    } catch { if (ticket === generation.current && !controller.signal.aborted) setState({ kind: "unavailable" }); }
  };
  const item = state.kind === "recognized" ? state.item : null;
  const tone = ["forbidden", "disabled", "sourceRequired", "invalid", "notRecognized", "changed", "limit"].includes(state.kind) ? "warning" : state.kind === "unavailable" ? "danger" : "neutral";
  const orders = state.kind === "recognized" ? state.orders : [];
  const preview = orders.find(order => order.id === previewId);
  const dates = new Intl.DateTimeFormat(i18n.resolvedLanguage === "sv" ? "sv-SE" : "en", { dateStyle: "medium", timeZone: "UTC" });
  return <section className="item-lookup" aria-labelledby="item-lookup-title">
    <p className="simulator-label">{t("lookup.context")}</p>
    <h2 id="item-lookup-title">{t("lookup.title")}</h2>
    <p className="scan-boundary" id="item-lookup-boundary">{t("lookup.boundary")}</p>
    <form aria-describedby="item-lookup-boundary" onSubmit={event => { event.preventDefault(); if (state.kind !== "loading") void run(); }}>
      <label htmlFor="item-lookup-fixture">{t("lookup.fixtureLabel")}</label>
      <div className="item-lookup__controls">
        <select id="item-lookup-fixture" value={choice} onChange={event => change(event.target.value as Fixture)}>
          {(Object.keys(fixtures) as Fixture[]).map(key => <option key={key} value={key}>{t(`lookup.fixture.${key}`)}</option>)}
        </select>
        <button className="item-lookup__button" type="submit" disabled={state.kind === "loading"}>{t("lookup.action")}</button>
      </div>
      <p className="item-lookup__tag" aria-label={t("lookup.tagLabel")}>{fixtures[choice]}</p>
    </form>
    <div className={`item-lookup__notice item-lookup__notice--${tone}`} role="status" aria-live="polite" aria-atomic="true">
      <span aria-hidden="true">{tone === "neutral" ? state.kind === "loading" ? "…" : "i" : "!"}</span>
      <p>{t(`lookup.message.${state.kind}`)}</p>
    </div>
    <dl className="item-lookup__fields">
      <div><dt>{t("lookup.item")}</dt><dd>{item?.displayName ?? "—"}</dd></div>
      <div><dt>{t("lookup.customer")}</dt><dd>{item?.customer.displayName ?? "—"}<span>{item?.customer.code ?? ""}</span></dd></div>
      <div><dt>{t("lookup.articleType")}</dt><dd>{item ? t(`lookup.article.${item.articleType}`) : "—"}</dd></div>
    </dl>
    <div className="scan-preview-orders">
      <h3>{t("preview.title")}</h3>
      <p className="scan-preview-orders__boundary">{t("preview.boundary")}</p>
      <p className="scan-preview-orders__basis">{t("preview.groupBasis", { date: dates.format(new Date(today + "T00:00:00Z")) })}</p>
      <p className="scan-preview-orders__activity">{state.kind === "recognized" ? state.lastSuccessfulPageAtUtc ? t("orders.activity", { time: new Intl.DateTimeFormat(i18n.resolvedLanguage === "sv" ? "sv-SE" : "en", { dateStyle: "medium", timeStyle: "short", timeZone: "Europe/Stockholm" }).format(new Date(state.lastSuccessfulPageAtUtc)) }) : t("orders.noActivity") : t("preview.notChecked")}</p>
      <p className="scan-preview-orders__message" role="status">{state.kind === "recognized" ? orders.length === 0 ? t("preview.none") : preview ? t("preview.viewing", { order: preview.orderNumber }) : t("preview.choose") : t("preview.wait")}</p>
      <div className="scan-preview-orders__list" role="region" aria-label={t("preview.listLabel")} tabIndex={0}>
        {orders.length ? groupPreviewOrders(orders, today).map(group => <section className={`scan-preview-orders__group scan-preview-orders__group--${group.key}`} aria-labelledby={`preview-group-${group.key}`} key={group.key}>
          <h4 id={`preview-group-${group.key}`}>{t(`preview.group.${group.key}`)} <span>({group.orders.length})</span></h4>
          {group.orders.length ? <ol>{group.orders.map(order => <li key={order.id}>
          <div><strong>{order.orderNumber}</strong><span>{t("orders.planned")}</span></div>
          <p>{t("preview.dates", { receiving: dates.format(new Date(order.plannedReceivingDate + "T00:00:00Z")), delivery: dates.format(new Date(order.plannedDeliveryDate + "T00:00:00Z")) })}</p>
          <button className="item-lookup__button" type="button" aria-pressed={order.id === previewId} onClick={() => setPreviewId(order.id)}>{t("preview.action", { order: order.orderNumber })}</button>
        </li>)}</ol> : <p className="scan-preview-orders__group-empty">{t("preview.groupEmpty")}</p>}
        </section>) : <p>{t("preview.noRows")}</p>}
      </div>
    </div>
  </section>;
}
