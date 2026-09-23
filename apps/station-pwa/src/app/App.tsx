import { useEffect, useState } from "react";
import type { LocalReceipt, SubmitScanV2 } from "@laundry/api-client";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";

import { checkGatewayReadiness } from "../gateway/readiness";
import { enrollDevelopmentBrowser } from "../gateway/development-enrollment";
import {
  checkSourceSession,
  type SourceSessionStatus,
} from "../gateway/source-session";
import {
  createSimulatedBarcodeScan,
  createSyntheticBarcodeIdentifier,
  submitScanToGateway,
  type ScanSubmissionOutcome,
} from "../gateway/scan-submission";
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
type ScanFeedback = {
  label: string;
  detail: string;
  tone: "success" | "warning" | "danger";
};

type AppProps = {
  checkGateway?: () => Promise<boolean>;
  checkEnrollment?: () => Promise<SourceSessionStatus>;
  enrollBrowser?: () => Promise<boolean>;
  allowDevelopmentEnrollment?: boolean;
  submitScan?: (request: SubmitScanV2) => Promise<ScanSubmissionOutcome>;
  createScanRequest?: (identifier: string) => SubmitScanV2;
};

function feedbackForReceipt(receipt: LocalReceipt, t: TFunction): ScanFeedback {
  if (receipt.status === "alreadyAcceptedLocally") {
    return {
      label: t("feedback.alreadySaved.label"),
      detail: t("feedback.alreadySaved.detail"),
      tone: "success",
    };
  }

  if (receipt.deliveryStatus === "synchronized") {
    return {
      label: t("feedback.synchronized.label"),
      detail: t("feedback.synchronized.detail"),
      tone: "success",
    };
  }

  if (receipt.deliveryStatus === "needsAttention") {
    return {
      label: t("feedback.needsAttention.label"),
      detail: t("feedback.needsAttention.detail"),
      tone: "warning",
    };
  }

  return {
    label: t("feedback.saved.label"),
    detail: t("feedback.saved.detail"),
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
  createScanRequest = createSimulatedBarcodeScan,
}: AppProps) {
  const { t, i18n } = useTranslation();
  const [connection, setConnection] = useState<ConnectionState>("checking");
  const [enrollment, setEnrollment] = useState<EnrollmentState>("checking");
  const [enrollmentAction, setEnrollmentAction] =
    useState<EnrollmentActionState>("idle");
  const [attempt, setAttempt] = useState(0);
  const [identifier, setIdentifier] = useState(createSyntheticBarcodeIdentifier);
  const [scanAction, setScanAction] = useState<ScanActionState>("idle");
  const [pendingScan, setPendingScan] = useState<SubmitScanV2 | null>(null);
  const [scanFeedback, setScanFeedback] = useState<ScanFeedback | null>(null);
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

  const handleScan = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (scanAction === "sending") return;

    const normalizedIdentifier = identifier.trim();
    if (!pendingScan && normalizedIdentifier.length === 0) {
      setScanFeedback({
        label: t("feedback.missingIdentifier.label"),
        detail: t("feedback.missingIdentifier.detail"),
        tone: "danger",
      });
      return;
    }

    const request = pendingScan ?? createScanRequest(normalizedIdentifier);
    setPendingScan(request);
    setScanFeedback(null);
    setScanAction("sending");

    const outcome = await submitScan(request);
    setScanAction("idle");

    if (outcome.kind === "uncertain") {
      setScanFeedback({
        label: t("feedback.unknown.label"),
        detail: t("feedback.unknown.detail"),
        tone: "warning",
      });
      return;
    }

    setPendingScan(null);
    if (outcome.kind === "rejected") {
      setScanFeedback({
        label: t("feedback.rejected.label"),
        detail: t("feedback.rejected.detail"),
        tone: "danger",
      });
      return;
    }

    setScanFeedback(feedbackForReceipt(outcome.receipt, t));
  };

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
            <p className="simulator-label">{t("scan.context")}</p>
            <h2 id="scan-simulator-title">{t("scan.title")}</h2>
            <p className="scan-boundary">{t("scan.boundary")}</p>
            <form onSubmit={(event) => void handleScan(event)}>
              <label htmlFor="synthetic-barcode">
                {t("scan.identifierLabel")}
              </label>
              <input
                id="synthetic-barcode"
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
                    : t("scan.send")}
              </button>
            </form>
            {scanFeedback ? (
              <div
                className={`scan-result scan-result--${scanFeedback.tone}`}
                role={scanFeedback.tone === "danger" ? "alert" : "status"}
                aria-live="polite"
              >
                <strong>{scanFeedback.label}</strong>
                <span>{scanFeedback.detail}</span>
              </div>
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
