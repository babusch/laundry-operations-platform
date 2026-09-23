import { useEffect, useState } from "react";
import type { LocalReceipt, SubmitScanV2 } from "@laundry/api-client";
import { useTranslation } from "react-i18next";

import { checkGatewayReadiness } from "../gateway/readiness";
import { enrollDevelopmentBrowser } from "../gateway/development-enrollment";
import {
  checkSourceSession,
  type SourceSessionStatus,
} from "../gateway/source-session";
import {
  createSimulatedScan,
  createSyntheticIdentifier,
  submitScanToGateway,
  type ScanTechnology,
  type ScanSubmissionOutcome,
} from "../gateway/scan-submission";
import {
  loadSyncAudit,
  type SyncAuditSnapshot,
} from "../gateway/sync-audit";
import {
  normalizeLanguage,
  persistLanguage,
  supportedLanguages,
} from "../i18n/i18n";
import type { SupportedLanguage } from "../i18n/resources";

type ConnectionState = "checking" | "ready" | "unavailable";
type EnrollmentState = "checking" | SourceSessionStatus;
type EnrollmentActionState = "idle" | "working" | "failed";
type ScanActionState = "idle" | "sending";
type AuditState = "idle" | "loading" | "ready" | "unavailable";
type ScanFeedback = {
  labelKey: string;
  detailKey: string;
  tone: "success" | "warning" | "danger";
};

type AppProps = {
  checkGateway?: () => Promise<boolean>;
  checkEnrollment?: () => Promise<SourceSessionStatus>;
  enrollBrowser?: () => Promise<boolean>;
  allowDevelopmentEnrollment?: boolean;
  submitScan?: (request: SubmitScanV2) => Promise<ScanSubmissionOutcome>;
  createScanRequest?: (
    technology: ScanTechnology,
    identifier: string,
  ) => SubmitScanV2;
  loadAudit?: () => Promise<SyncAuditSnapshot | null>;
};

function feedbackForReceipt(receipt: LocalReceipt): ScanFeedback {
  if (receipt.status === "alreadyAcceptedLocally") {
    return {
      labelKey: "feedback.alreadySaved.label",
      detailKey: "feedback.alreadySaved.detail",
      tone: "success",
    };
  }

  if (receipt.deliveryStatus === "synchronized") {
    return {
      labelKey: "feedback.synchronized.label",
      detailKey: "feedback.synchronized.detail",
      tone: "success",
    };
  }

  if (receipt.deliveryStatus === "needsAttention") {
    return {
      labelKey: "feedback.needsAttention.label",
      detailKey: "feedback.needsAttention.detail",
      tone: "warning",
    };
  }

  return {
    labelKey: "feedback.saved.label",
    detailKey: "feedback.saved.detail",
    tone: "success",
  };
}

const connectionContent: Record<
  ConnectionState,
  { icon: string; labelKey: string; detailKey: string }
> = {
  checking: {
    icon: "…",
    labelKey: "connection.checking.label",
    detailKey: "connection.checking.detail",
  },
  ready: {
    icon: "✓",
    labelKey: "connection.ready.label",
    detailKey: "connection.ready.detail",
  },
  unavailable: {
    icon: "!",
    labelKey: "connection.unavailable.label",
    detailKey: "connection.unavailable.detail",
  },
};

const enrollmentContent: Record<
  EnrollmentState,
  { icon: string; labelKey: string; detailKey: string; tone: string }
> = {
  checking: {
    icon: "…",
    labelKey: "enrollment.checking.label",
    detailKey: "enrollment.checking.detail",
    tone: "checking",
  },
  enrolled: {
    icon: "✓",
    labelKey: "enrollment.enrolled.label",
    detailKey: "enrollment.enrolled.detail",
    tone: "ready",
  },
  notEnrolled: {
    icon: "!",
    labelKey: "enrollment.notEnrolled.label",
    detailKey: "enrollment.notEnrolled.detail",
    tone: "warning",
  },
  unavailable: {
    icon: "!",
    labelKey: "enrollment.unavailable.label",
    detailKey: "enrollment.unavailable.detail",
    tone: "unavailable",
  },
};

export function App({
  checkGateway = checkGatewayReadiness,
  checkEnrollment = checkSourceSession,
  enrollBrowser = enrollDevelopmentBrowser,
  allowDevelopmentEnrollment = window.location.protocol === "https:",
  submitScan = submitScanToGateway,
  createScanRequest = createSimulatedScan,
  loadAudit = loadSyncAudit,
}: AppProps) {
  const { t, i18n } = useTranslation();
  const [connection, setConnection] = useState<ConnectionState>("checking");
  const [enrollment, setEnrollment] = useState<EnrollmentState>("checking");
  const [enrollmentAction, setEnrollmentAction] =
    useState<EnrollmentActionState>("idle");
  const [attempt, setAttempt] = useState(0);
  const [technology, setTechnology] = useState<ScanTechnology>("rfid");
  const [identifier, setIdentifier] = useState(() =>
    createSyntheticIdentifier("rfid"),
  );
  const [scanAction, setScanAction] = useState<ScanActionState>("idle");
  const [pendingScan, setPendingScan] = useState<SubmitScanV2 | null>(null);
  const [scanFeedback, setScanFeedback] = useState<ScanFeedback | null>(null);
  const [auditState, setAuditState] = useState<AuditState>("idle");
  const [auditSnapshot, setAuditSnapshot] =
    useState<SyncAuditSnapshot | null>(null);
  const [auditAttempt, setAuditAttempt] = useState(0);
  const activeLanguage = normalizeLanguage(i18n.resolvedLanguage) ?? "en";

  useEffect(() => {
    document.documentElement.lang = activeLanguage;
    document.title = t("meta.title");
    document
      .querySelector('meta[name="description"]')
      ?.setAttribute("content", t("meta.description"));
    persistLanguage(activeLanguage);
  }, [activeLanguage, t]);

  useEffect(() => {
    let active = true;
    setConnection("checking");
    setEnrollment("checking");
    setEnrollmentAction("idle");

    void (async () => {
      try {
        const ready = await checkGateway();
        if (!active) return;
        if (!ready) {
          setConnection("unavailable");
          return;
        }

        setConnection("ready");
        try {
          const status = await checkEnrollment();
          if (active) setEnrollment(status);
        } catch {
          if (active) setEnrollment("unavailable");
        }
      } catch {
        if (active) setConnection("unavailable");
      }
    })();

    return () => {
      active = false;
    };
  }, [attempt, checkEnrollment, checkGateway]);

  useEffect(() => {
    if (connection !== "ready" || enrollment !== "enrolled") {
      setAuditState("idle");
      setAuditSnapshot(null);
      return;
    }

    let active = true;
    setAuditState("loading");

    void loadAudit().then((snapshot) => {
      if (!active) return;
      setAuditSnapshot(snapshot);
      setAuditState(snapshot ? "ready" : "unavailable");
    });

    return () => {
      active = false;
    };
  }, [auditAttempt, connection, enrollment, loadAudit]);

  const content = connectionContent[connection];
  const enrollmentStatus = enrollmentContent[enrollment];
  const retryAvailable =
    connection === "unavailable" || enrollment === "unavailable";

  const handleEnrollment = async () => {
    setEnrollmentAction("working");

    try {
      if (!(await enrollBrowser())) {
        setEnrollmentAction("failed");
        return;
      }

      setEnrollment("checking");
      const status = await checkEnrollment();
      setEnrollment(status);
      setEnrollmentAction(status === "enrolled" ? "idle" : "failed");
    } catch {
      setEnrollmentAction("failed");
    }
  };

  const handleLanguageChange = (language: SupportedLanguage) => {
    void i18n.changeLanguage(language);
  };

  const handleTechnologyChange = (nextTechnology: ScanTechnology) => {
    setTechnology(nextTechnology);
    setIdentifier(createSyntheticIdentifier(nextTechnology));
    setScanFeedback(null);
  };

  const handleScan = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (scanAction === "sending") return;

    const normalizedIdentifier = identifier.trim();
    if (!pendingScan && normalizedIdentifier.length === 0) {
      setScanFeedback({
        labelKey: "feedback.missingIdentifier.label",
        detailKey: "feedback.missingIdentifier.detail",
        tone: "danger",
      });
      return;
    }

    const request =
      pendingScan ?? createScanRequest(technology, normalizedIdentifier);
    setPendingScan(request);
    setScanFeedback(null);
    setScanAction("sending");

    const outcome = await submitScan(request);
    setScanAction("idle");

    if (outcome.kind === "uncertain") {
      setScanFeedback({
        labelKey: "feedback.unknown.label",
        detailKey: "feedback.unknown.detail",
        tone: "warning",
      });
      return;
    }

    setPendingScan(null);
    if (outcome.kind === "rejected") {
      setScanFeedback({
        labelKey: "feedback.rejected.label",
        detailKey: "feedback.rejected.detail",
        tone: "danger",
      });
      return;
    }

    setScanFeedback(feedbackForReceipt(outcome.receipt));
    setAuditAttempt((value) => value + 1);
  };

  const auditTimeFormatter = new Intl.DateTimeFormat(
    activeLanguage === "sv" ? "sv-SE" : "en",
    { dateStyle: "medium", timeStyle: "short" },
  );

  return (
    <main className="app-shell">
      <section className="foundation" aria-labelledby="station-title">
        <div className="station-header">
          <div>
            <p className="environment-label">{t("environment")}</p>
            <h1 id="station-title">{t("stationTitle")}</h1>
          </div>
          <label className="language-control">
            <span>{t("language.label")}</span>
            <select
              value={activeLanguage}
              onChange={(event) =>
                handleLanguageChange(event.target.value as SupportedLanguage)
              }
            >
              {supportedLanguages.map((language) => (
                <option key={language} value={language}>
                  {language === "en"
                    ? t("language.english")
                    : t("language.swedish")}
                </option>
              ))}
            </select>
          </label>
        </div>
        <div
          className={`status-card status-card--${connection}`}
          role="status"
          aria-live="polite"
        >
          <span className="status-mark" aria-hidden="true">
            {content.icon}
          </span>
          <div>
            <p className="status-line">{t(content.labelKey)}</p>
            <p className="status-detail">{t(content.detailKey)}</p>
          </div>
        </div>
        {connection === "ready" ? (
          <div
            className={`status-card status-card--${enrollmentStatus.tone}`}
            role="status"
            aria-live="polite"
          >
            <span className="status-mark" aria-hidden="true">
              {enrollmentStatus.icon}
            </span>
            <div>
              <p className="status-line">{t(enrollmentStatus.labelKey)}</p>
              <p className="status-detail">{t(enrollmentStatus.detailKey)}</p>
            </div>
          </div>
        ) : null}
        {retryAvailable ? (
          <button className="retry-button" onClick={() => setAttempt((value) => value + 1)}>
            {t("actions.retryConnection")}
          </button>
        ) : null}
        {connection === "ready" &&
        enrollment === "notEnrolled" &&
        allowDevelopmentEnrollment ? (
          <div className="setup-action">
            <button
              className="setup-button"
              disabled={enrollmentAction === "working"}
              onClick={() => void handleEnrollment()}
            >
              {enrollmentAction === "working"
                ? t("actions.settingUpBrowser")
                : enrollmentAction === "failed"
                  ? t("actions.retrySetup")
                  : t("actions.setupBrowser")}
            </button>
            {enrollmentAction === "failed" ? (
              <p className="action-error" role="alert">
                {t("setup.failed")}
              </p>
            ) : null}
          </div>
        ) : null}
        {connection === "ready" &&
        enrollment === "notEnrolled" &&
        !allowDevelopmentEnrollment ? (
          <p className="setup-guidance">
            {t("setup.httpsGuidance")}
          </p>
        ) : null}
        {connection === "ready" && enrollment === "enrolled" ? (
          <section className="scan-simulator" aria-labelledby="scan-simulator-title">
            <p className="simulator-label">
              {t(`scan.technology.${technology}.context`)}
            </p>
            <h2 id="scan-simulator-title">
              {t(`scan.technology.${technology}.title`)}
            </h2>
            <p className="scan-boundary">{t("scan.boundary")}</p>
            <form onSubmit={(event) => void handleScan(event)}>
              <fieldset
                className="technology-picker"
                disabled={scanAction === "sending" || pendingScan !== null}
              >
                <legend>{t("scan.technologyLabel")}</legend>
                <div className="technology-options">
                  {(["rfid", "barcode"] as const).map((option) => (
                    <label
                      key={option}
                      className={technology === option ? "is-selected" : undefined}
                    >
                      <input
                        type="radio"
                        name="scan-technology"
                        value={option}
                        checked={technology === option}
                        onChange={() => handleTechnologyChange(option)}
                      />
                      <span>{t(`scan.technology.${option}.name`)}</span>
                    </label>
                  ))}
                </div>
              </fieldset>
              <label htmlFor="synthetic-identifier">
                {t(`scan.technology.${technology}.identifierLabel`)}
              </label>
              <input
                id="synthetic-identifier"
                type="text"
                value={identifier}
                maxLength={512}
                disabled={scanAction === "sending" || pendingScan !== null}
                autoComplete="off"
                spellCheck={false}
                onChange={(event) => setIdentifier(event.target.value)}
              />
              <button className="scan-button" disabled={scanAction === "sending"}>
                {scanAction === "sending"
                  ? t("scan.saving")
                  : pendingScan
                    ? t("scan.retrySame")
                    : t(`scan.technology.${technology}.send`)}
              </button>
            </form>
            {scanFeedback ? (
              <div
                className={`scan-result scan-result--${scanFeedback.tone}`}
                role={scanFeedback.tone === "danger" ? "alert" : "status"}
                aria-live="polite"
              >
                <strong>{t(scanFeedback.labelKey)}</strong>
                <span>{t(scanFeedback.detailKey)}</span>
              </div>
            ) : null}
          </section>
        ) : null}
        {connection === "ready" && enrollment === "enrolled" ? (
          <section className="sync-audit" aria-labelledby="sync-audit-title">
            <div className="sync-audit__header">
              <div>
                <p className="simulator-label">{t("audit.context")}</p>
                <h2 id="sync-audit-title">{t("audit.title")}</h2>
              </div>
              <button
                className="audit-refresh-button"
                disabled={auditState === "loading"}
                onClick={() => setAuditAttempt((value) => value + 1)}
              >
                {auditState === "loading"
                  ? t("audit.refreshing")
                  : t("audit.refresh")}
              </button>
            </div>
            <p className="scan-boundary">{t("audit.description")}</p>
            {auditState === "loading" ? (
              <p className="audit-message" role="status">
                {t("audit.loading")}
              </p>
            ) : null}
            {auditState === "unavailable" ? (
              <p className="audit-message audit-message--warning" role="status">
                {t("audit.unavailable")}
              </p>
            ) : null}
            {auditState === "ready" && auditSnapshot ? (
              <>
                <dl className="audit-summary">
                  <div>
                    <dt>{t("audit.status.pending")}</dt>
                    <dd>{auditSnapshot.summary.pending}</dd>
                  </div>
                  <div>
                    <dt>{t("audit.status.synchronized")}</dt>
                    <dd>{auditSnapshot.summary.synchronized}</dd>
                  </div>
                  <div>
                    <dt>{t("audit.status.needsAttention")}</dt>
                    <dd>{auditSnapshot.summary.needsAttention}</dd>
                  </div>
                </dl>
                <h3>{t("audit.recentTitle")}</h3>
                {auditSnapshot.recent.length === 0 ? (
                  <p className="audit-message">{t("audit.empty")}</p>
                ) : (
                  <ol className="audit-events">
                    {auditSnapshot.recent.map((item) => (
                      <li key={item.eventId}>
                        <span
                          className={`audit-status audit-status--${item.status}`}
                        >
                          {t(`audit.status.${item.status}`)}
                        </span>
                        <time dateTime={item.acceptedAtUtc}>
                          {auditTimeFormatter.format(new Date(item.acceptedAtUtc))}
                        </time>
                      </li>
                    ))}
                  </ol>
                )}
              </>
            ) : null}
          </section>
        ) : null}
        <p className="scope-note">
          {t("scopeNote")}
        </p>
      </section>
    </main>
  );
}
