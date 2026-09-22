import { useEffect, useState } from "react";
import type { LocalReceipt, SubmitScanV2 } from "@laundry/api-client";

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

function feedbackForReceipt(receipt: LocalReceipt): ScanFeedback {
  if (receipt.status === "alreadyAcceptedLocally") {
    return {
      label: "Already saved locally",
      detail: "The gateway recognized this unchanged retry and did not duplicate it.",
      tone: "success",
    };
  }

  if (receipt.deliveryStatus === "synchronized") {
    return {
      label: "Saved locally and synchronized",
      detail: "The gateway durably stored this observation and the cloud confirmed it.",
      tone: "success",
    };
  }

  if (receipt.deliveryStatus === "needsAttention") {
    return {
      label: "Saved locally — synchronization needs attention",
      detail: "The observation is safe at this plant, but cloud delivery needs review.",
      tone: "warning",
    };
  }

  return {
    label: "Saved locally",
    detail: "The observation is safe at this plant and waiting for cloud synchronization.",
    tone: "success",
  };
}

const connectionContent: Record<
  ConnectionState,
  { icon: string; label: string; detail: string }
> = {
  checking: {
    icon: "…",
    label: "Checking gateway…",
    detail: "Confirming that durable local storage is available.",
  },
  ready: {
    icon: "✓",
    label: "Gateway ready",
    detail: "Local plant storage is available. Cloud status is not checked yet.",
  },
  unavailable: {
    icon: "!",
    label: "Gateway unavailable",
    detail: "Local storage cannot be confirmed. Scans cannot be saved.",
  },
};

const enrollmentContent: Record<
  EnrollmentState,
  { icon: string; label: string; detail: string; tone: string }
> = {
  checking: {
    icon: "…",
    label: "Checking station setup…",
    detail: "Confirming whether this browser is enrolled with the local gateway.",
    tone: "checking",
  },
  enrolled: {
    icon: "✓",
    label: "Station setup complete",
    detail:
      "This browser is enrolled with the local gateway. Operator sign-in is not implemented yet.",
    tone: "ready",
  },
  notEnrolled: {
    icon: "!",
    label: "Station setup required",
    detail: "This browser has not been enrolled with the local gateway.",
    tone: "warning",
  },
  unavailable: {
    icon: "!",
    label: "Station setup unavailable",
    detail: "The gateway answered, but station enrollment could not be checked.",
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
  const [connection, setConnection] = useState<ConnectionState>("checking");
  const [enrollment, setEnrollment] = useState<EnrollmentState>("checking");
  const [enrollmentAction, setEnrollmentAction] =
    useState<EnrollmentActionState>("idle");
  const [attempt, setAttempt] = useState(0);
  const [identifier, setIdentifier] = useState(createSyntheticBarcodeIdentifier);
  const [scanAction, setScanAction] = useState<ScanActionState>("idle");
  const [pendingScan, setPendingScan] = useState<SubmitScanV2 | null>(null);
  const [scanFeedback, setScanFeedback] = useState<ScanFeedback | null>(null);

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

  const handleScan = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (scanAction === "sending") return;

    const normalizedIdentifier = identifier.trim();
    if (!pendingScan && normalizedIdentifier.length === 0) {
      setScanFeedback({
        label: "Not saved",
        detail: "Enter a synthetic barcode identifier before sending.",
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
        label: "Save result unknown",
        detail: "Retry the same scan. The gateway will not create a duplicate.",
        tone: "warning",
      });
      return;
    }

    setPendingScan(null);
    if (outcome.kind === "rejected") {
      setScanFeedback({
        label: "Not saved",
        detail: "Check the station connection and identifier, then try again.",
        tone: "danger",
      });
      return;
    }

    setScanFeedback(feedbackForReceipt(outcome.receipt));
  };

  return (
    <main className="app-shell">
      <section className="foundation" aria-labelledby="station-title">
        <p className="environment-label">Development simulator</p>
        <h1 id="station-title">Laundry station</h1>
        <div
          className={`status-card status-card--${connection}`}
          role="status"
          aria-live="polite"
        >
          <span className="status-mark" aria-hidden="true">
            {content.icon}
          </span>
          <div>
            <p className="status-line">{content.label}</p>
            <p className="status-detail">{content.detail}</p>
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
              <p className="status-line">{enrollmentStatus.label}</p>
              <p className="status-detail">{enrollmentStatus.detail}</p>
            </div>
          </div>
        ) : null}
        {retryAvailable ? (
          <button className="retry-button" onClick={() => setAttempt((value) => value + 1)}>
            Retry connection
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
                ? "Setting up browser…"
                : enrollmentAction === "failed"
                  ? "Try setup again"
                  : "Set up this browser"}
            </button>
            {enrollmentAction === "failed" ? (
              <p className="action-error" role="alert">
                Station setup could not be completed. Try again.
              </p>
            ) : null}
          </div>
        ) : null}
        {connection === "ready" &&
        enrollment === "notEnrolled" &&
        !allowDevelopmentEnrollment ? (
          <p className="setup-guidance">
            Open this station from the gateway HTTPS address to set up this browser.
          </p>
        ) : null}
        {connection === "ready" && enrollment === "enrolled" ? (
          <section className="scan-simulator" aria-labelledby="scan-simulator-title">
            <p className="simulator-label">Development only · Barcode</p>
            <h2 id="scan-simulator-title">Simulated barcode scan</h2>
            <p className="scan-boundary">
              This records a raw observation only. No operator is signed in, and it
              does not record receiving, sorting, packing, dispatch, or another
              laundry operation.
            </p>
            <form onSubmit={(event) => void handleScan(event)}>
              <label htmlFor="synthetic-barcode">Synthetic barcode identifier</label>
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
                  ? "Saving to local gateway…"
                  : pendingScan
                    ? "Retry same scan"
                    : "Send simulated barcode"}
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
          This Development-only application proves source enrollment and raw scan
          storage. Operator sign-in and production workflow controls are not
          implemented.
        </p>
      </section>
    </main>
  );
}
