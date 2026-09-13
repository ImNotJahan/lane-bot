namespace Lane.Host.Web;

/// <summary>The configuration editor, opened as its own window from the dashboard.</summary>
public static class ConfigPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>lane · config</title>
        <style>
          :root { color-scheme: dark; }
          * { box-sizing: border-box; }
          body {
            margin: 0; background: #0d0f12; color: #d7dbe0; height: 100vh; display: flex; flex-direction: column;
            font: 13px/1.4 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
          }
          header {
            padding: 10px 16px; border-bottom: 1px solid #23272e;
            display: flex; align-items: center; gap: 12px;
          }
          header h1 { font-size: 14px; margin: 0; letter-spacing: 0.08em; text-transform: uppercase; color: #8fb4ff; }
          header .path { color: #6b7280; font-size: 11.5px; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
          button {
            font: inherit; background: #1c2027; color: #d7dbe0; border: 1px solid #2c313a; border-radius: 5px;
            padding: 6px 12px; cursor: pointer;
          }
          button:hover { background: #23272e; }
          button.primary { background: #2b5f4a; border-color: #3a7a5f; color: #d8ffe9; }
          button.primary:hover { background: #33714f; }
          button.danger { background: #5f2b2b; border-color: #7a3a3a; color: #ffd8d8; }
          button.danger:hover { background: #713333; }
          button:disabled { opacity: 0.5; cursor: default; }
          #editor {
            flex: 1; margin: 0; border: 0; resize: none; padding: 14px 16px;
            background: #14171c; color: #d7dbe0; font: inherit; tab-size: 2;
          }
          #status { padding: 6px 16px; font-size: 11.5px; color: #6b7280; border-top: 1px solid #23272e; min-height: 1.4em; }
          #status.error { color: #ff9494; }
          #status.ok { color: #59d18a; }
        </style>
        </head>
        <body>
        <header>
          <h1>config</h1>
          <span class="path" id="path"></span>
          <button id="saveBtn" class="primary">Save</button>
          <button id="reloadBtn" class="danger">Reload configuration</button>
        </header>
        <textarea id="editor" spellcheck="false" placeholder="loading…"></textarea>
        <div id="status"></div>
        <script>
          const el = id => document.getElementById(id);
          const editor = el("editor");
          const status = el("status");

          function setStatus(text, kind) {
            status.textContent = text;
            status.className = kind || "";
          }

          async function load() {
            try {
              const res = await fetch("/api/config");
              const data = await res.json();
              if (!res.ok) throw new Error(data.error || res.statusText);
              editor.value = data.content;
              el("path").textContent = data.path;
              setStatus("Loaded.", "ok");
            } catch (e) {
              setStatus("Failed to load: " + e.message, "error");
            }
          }

          async function save() {
            let parsed;
            try {
              parsed = JSON.parse(editor.value);
            } catch (e) {
              setStatus("Not valid JSON: " + e.message, "error");
              return;
            }

            el("saveBtn").disabled = true;
            try {
              const res = await fetch("/api/config", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(parsed, null, 2)
              });
              const data = await res.json();
              if (!res.ok) throw new Error(data.error || res.statusText);
              setStatus("Saved. Changes take effect after a reload.", "ok");
            } catch (e) {
              setStatus("Failed to save: " + e.message, "error");
            } finally {
              el("saveBtn").disabled = false;
            }
          }

          async function reload() {
            if (!confirm("Save and restart Lane with the current configuration?")) return;

            await save();

            el("reloadBtn").disabled = true;
            setStatus("Restarting…", "ok");

            try {
              await fetch("/api/restart", { method: "POST" });
            } catch (e) {
              // The process is exiting to relaunch; a dropped connection is expected here.
            }

            setStatus("Restarting — this window will not update automatically once Lane is back.", "ok");
          }

          el("saveBtn").addEventListener("click", save);
          el("reloadBtn").addEventListener("click", reload);

          editor.addEventListener("keydown", e => {
            if ((e.metaKey || e.ctrlKey) && e.key === "s") {
              e.preventDefault();
              save();
            }
          });

          load();
        </script>
        </body>
        </html>
        """;
}
