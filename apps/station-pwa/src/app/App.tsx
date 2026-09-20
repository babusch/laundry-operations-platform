export function App() {
  return (
    <main className="app-shell">
      <section className="foundation" aria-labelledby="station-title">
        <p className="environment-label">Development simulator</p>
        <h1 id="station-title">Laundry station</h1>
        <p className="status-line">
          <span className="status-mark" aria-hidden="true">
            ✓
          </span>
          Application foundation ready
        </p>
        <p className="scope-note">
          No scans are sent yet. Station enrollment and scan controls will be
          added only after we review them.
        </p>
      </section>
    </main>
  );
}
