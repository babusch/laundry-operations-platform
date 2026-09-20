import { useEffect, useState } from "react";

import { checkGatewayReadiness } from "../gateway/readiness";

type ConnectionState = "checking" | "ready" | "unavailable";

type AppProps = {
  checkGateway?: () => Promise<boolean>;
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

export function App({ checkGateway = checkGatewayReadiness }: AppProps) {
  const [connection, setConnection] = useState<ConnectionState>("checking");
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let active = true;
    setConnection("checking");

    void checkGateway()
      .then((ready) => {
        if (active) setConnection(ready ? "ready" : "unavailable");
      })
      .catch(() => {
        if (active) setConnection("unavailable");
      });

    return () => {
      active = false;
    };
  }, [attempt, checkGateway]);

  const content = connectionContent[connection];

  return (
    <main className="app-shell">
      <section className="foundation" aria-labelledby="station-title">
        <p className="environment-label">Development simulator</p>
        <h1 id="station-title">Laundry station</h1>
        <div
          className={`connection-status connection-status--${connection}`}
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
        {connection === "unavailable" ? (
          <button className="retry-button" onClick={() => setAttempt((value) => value + 1)}>
            Retry connection
          </button>
        ) : null}
        <p className="scope-note">
          No scans are sent yet. Station enrollment and scan controls will be
          added only after we review them.
        </p>
      </section>
    </main>
  );
}
