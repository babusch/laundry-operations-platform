import { useEffect, useState } from "react";

import { checkGatewayReadiness } from "../gateway/readiness";
import { enrollDevelopmentBrowser } from "../gateway/development-enrollment";
import {
  checkSourceSession,
  type SourceSessionStatus,
} from "../gateway/source-session";

type ConnectionState = "checking" | "ready" | "unavailable";
type EnrollmentState = "checking" | SourceSessionStatus;
type EnrollmentActionState = "idle" | "working" | "failed";

type AppProps = {
  checkGateway?: () => Promise<boolean>;
  checkEnrollment?: () => Promise<SourceSessionStatus>;
  enrollBrowser?: () => Promise<boolean>;
  allowDevelopmentEnrollment?: boolean;
};

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
}: AppProps) {
  const [connection, setConnection] = useState<ConnectionState>("checking");
  const [enrollment, setEnrollment] = useState<EnrollmentState>("checking");
  const [enrollmentAction, setEnrollmentAction] =
    useState<EnrollmentActionState>("idle");
  const [attempt, setAttempt] = useState(0);

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
        <p className="scope-note">
          This Development-only setup creates a local browser enrollment. No
          operator is signed in and no scans are sent yet.
        </p>
      </section>
    </main>
  );
}
