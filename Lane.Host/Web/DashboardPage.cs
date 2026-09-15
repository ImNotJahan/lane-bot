namespace Lane.Host.Web;

/// <summary>The dashboard's single page. Self-contained so the server needs no static files.</summary>
public static class DashboardPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>lane</title>
        <style>
          :root { color-scheme: dark; }
          * { box-sizing: border-box; }
          body {
            margin: 0; background: #0d0f12; color: #d7dbe0;
            font: 13px/1.4 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
          }
          header {
            padding: 10px 16px; border-bottom: 1px solid #23272e;
            display: flex; align-items: center; gap: 12px;
          }
          header h1 { font-size: 14px; margin: 0; letter-spacing: 0.08em; text-transform: uppercase; color: #8fb4ff; }
          header .dot { width: 8px; height: 8px; border-radius: 50%; background: #4a5058; }
          header .dot.live { background: #59d18a; }
          header a {
            margin-left: auto; color: #8fb4ff; text-decoration: none; font-size: 12px;
            border: 1px solid #2c313a; border-radius: 5px; padding: 5px 10px;
          }
          header a:hover { background: #1c2027; }
          main { display: grid; grid-template-columns: 1fr 1fr; grid-template-rows: auto auto 1fr; gap: 12px; padding: 12px; height: calc(100vh - 45px); }
          .panel { background: #14171c; border: 1px solid #23272e; border-radius: 6px; padding: 10px 12px; overflow: auto; }
          .panel h2 { font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: #8892a0; margin: 0 0 8px; }
          .span2 { grid-column: 1 / span 2; }
          table { width: 100%; border-collapse: collapse; font-size: 12px; }
          th, td { text-align: left; padding: 3px 8px 3px 0; white-space: nowrap; }
          th { color: #6b7280; font-weight: 600; }
          tr:nth-child(even) td { background: rgba(255,255,255,0.02); }
          .bar { height: 10px; border-radius: 5px; background: #23272e; overflow: hidden; margin: 6px 0; }
          .bar > div { height: 100%; background: linear-gradient(90deg, #59d18a, #8fb4ff); }
          .muted { color: #6b7280; }
          #log { font-size: 11.5px; white-space: pre-wrap; word-break: break-all; }
          #log div { padding: 1px 0; border-bottom: 1px dotted rgba(255,255,255,0.03); }
          .logsPanel { grid-row: 3; }
        </style>
        </head>
        <body>
        <header>
          <h1>lane</h1>
          <span class="dot" id="liveDot"></span>
          <span class="muted" id="asOf"></span>
          <a href="/config" target="_blank" rel="noopener">Config ↗</a>
        </header>
        <main>
          <section class="panel">
            <h2>Energy</h2>
            <div class="bar"><div id="energyBar" style="width:0%"></div></div>
            <div id="energyText" class="muted"></div>
          </section>
          <section class="panel">
            <h2>Inner Life</h2>
            <div id="monoNext"></div>
            <div id="monoThought" class="muted" style="margin-top:6px"></div>
          </section>
          <section class="panel span2">
            <h2>Sessions</h2>
            <table>
              <thead><tr><th>session</th><th>name</th><th>state</th><th>activity</th><th>idle</th></tr></thead>
              <tbody id="sessions"></tbody>
            </table>
          </section>
          <section class="panel span2">
            <h2>Models</h2>
            <table>
              <thead><tr><th>model</th><th>roles</th><th>calls</th><th>in</th><th>out</th><th>cached</th><th>hit</th><th>last</th></tr></thead>
              <tbody id="models"></tbody>
            </table>
          </section>
          <section class="panel span2 logsPanel">
            <h2>Log</h2>
            <div id="log"></div>
          </section>
        </main>
        <script src="/auth.js"></script>
        <script>
          const el = id => document.getElementById(id);

          function idle(iso) {
            const s = (Date.now() - new Date(iso).getTime()) / 1000;
            if (s < 60) return Math.floor(s) + "s";
            if (s < 3600) return Math.floor(s / 60) + "m";
            return Math.floor(s / 3600) + "h";
          }

          function countdown(iso) {
            if (!iso) return "—";
            const s = (new Date(iso).getTime() - Date.now()) / 1000;
            if (s <= 0) return "any moment";
            if (s < 60) return Math.floor(s) + "s";
            if (s < 3600) return Math.floor(s / 60) + "m " + Math.floor(s % 60) + "s";
            return Math.floor(s / 3600) + "h " + Math.floor((s % 3600) / 60) + "m";
          }

          function compact(n) {
            const abs = Math.abs(n);
            if (abs >= 1e6) return (n / 1e6).toFixed(1) + "M";
            if (abs >= 1e3) return (n / 1e3).toFixed(1) + "k";
            return n.toLocaleString();
          }

          function render(snap) {
            el("asOf").textContent = new Date().toLocaleTimeString();

            el("energyBar").style.width = Math.round(snap.energy.fraction * 100) + "%";
            el("energyText").textContent = snap.energy.asleep
              ? `asleep — rested in ${countdown(new Date(Date.now() + snap.energy.restedInSeconds * 1000).toISOString())}`
              : `${compact(snap.energy.remaining)} / ${compact(snap.energy.budget)} tokens — ${snap.energy.tier.toLowerCase()}`;

            el("monoNext").textContent = "next thought: " + countdown(snap.monologue.nextThoughtAt) +
              (snap.monologue.thinking ? "  (thinking)" : "");
            el("monoThought").textContent = snap.monologue.lastThought || "(nothing yet)";

            el("sessions").innerHTML = snap.sessions.length
              ? snap.sessions.map(s => `<tr><td>${s.id}</td><td>${s.name}</td><td>${s.state}</td><td>${s.activity}</td><td>${idle(s.lastActivity)}</td></tr>`).join("")
              : `<tr><td class="muted" colspan="5">(no sessions)</td></tr>`;

            el("models").innerHTML = snap.models.length
              ? snap.models.map(m => `<tr><td>${m.instance}</td><td>${m.roles}</td><td>${m.calls}</td><td>${m.input}</td><td>${m.output}</td><td>${m.cacheRead}</td><td>${m.cacheRate}</td><td>${m.lastLatencyMs ? m.lastLatencyMs + "ms" : "-"}</td></tr>`).join("")
              : `<tr><td class="muted" colspan="8">(none)</td></tr>`;

            const log = el("log");
            const atBottom = log.scrollTop + log.clientHeight >= log.scrollHeight - 4;
            log.innerHTML = snap.logs.map(l => `<div>${l.replace(/&/g,"&amp;").replace(/</g,"&lt;")}</div>`).join("");
            if (atBottom) log.scrollTop = log.scrollHeight;
          }

          async function poll() {
            try {
              const res = await laneFetch("/api/snapshot");
              render(await res.json());
              el("liveDot").classList.add("live");
            } catch (e) {
              el("liveDot").classList.remove("live");
            } finally {
              setTimeout(poll, 1500);
            }
          }

          poll();
        </script>
        </body>
        </html>
        """;
}
