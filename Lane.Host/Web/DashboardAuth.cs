namespace Lane.Host.Web;

/// <summary>
/// Served at <c>/auth.js</c>. Defines <c>laneFetch</c>, which attaches the stored sign-in token and, on a 401, waits for
/// the owner to sign in through the node app's portal popup (the same relay the portal uses) before retrying.
/// </summary>
public static class DashboardAuth
{
    public const string Script = """
        (() => {
          const TOKEN_KEY = "lane-dashboard-token";
          const NODE_APP_PORTAL = "http://localhost:5075/lane/";
          const waiting = [];
          let popup = null;

          const readToken = () => { try { return localStorage.getItem(TOKEN_KEY); } catch { return null; } };
          const writeToken = value => {
            try { value ? localStorage.setItem(TOKEN_KEY, value) : localStorage.removeItem(TOKEN_KEY); } catch { }
          };

          const overlay = document.createElement("div");
          overlay.hidden = true;
          overlay.style.cssText = "position:fixed;inset:0;z-index:10;display:flex;align-items:center;justify-content:center;background:rgba(13,15,18,.92);font:13px/1.4 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;color:#d7dbe0";
          overlay.innerHTML = `
            <div style="background:#14171c;border:1px solid #23272e;border-radius:6px;padding:16px 18px;max-width:420px;display:grid;gap:10px">
              <strong style="color:#8fb4ff;letter-spacing:.08em;text-transform:uppercase;font-size:12px">sign in</strong>
              <div data-reason style="color:#6b7280"></div>
              <div>Your node app must be running on this device at <code>http://localhost:5075</code>.</div>
              <button type="button" style="font:inherit;background:#2b5f4a;border:1px solid #3a7a5f;color:#d8ffe9;border-radius:5px;padding:6px 12px;cursor:pointer;justify-self:start">Sign in with security key</button>
              <div data-error style="color:#ff9494" hidden></div>
            </div>`;

          const reason = overlay.querySelector("[data-reason]");
          const error = overlay.querySelector("[data-error]");

          overlay.querySelector("button").addEventListener("click", () => {
            error.hidden = true;
            popup = window.open(`${NODE_APP_PORTAL}?relay=${encodeURIComponent(location.origin)}`, "lane-sign-in", "popup,width=560,height=640");
            if (!popup) {
              error.textContent = "Allow popups for this page, then try again.";
              error.hidden = false;
            }
          });

          function signedIn() {
            overlay.hidden = true;
            waiting.splice(0).forEach(resolve => resolve());
          }

          window.addEventListener("message", event => {
            if (!popup || event.source !== popup || event.data?.type !== "lane-sign-in") return;
            popup = null;
            writeToken(event.data.token);
            signedIn();
          });

          window.addEventListener("storage", event => {
            if (event.key === TOKEN_KEY && event.newValue) signedIn();
          });

          window.laneFetch = async (path, options = {}) => {
            for (;;) {
              const token = readToken();
              const headers = { ...options.headers };
              if (token) headers.Authorization = `Bearer ${token}`;
              const response = await fetch(path, { ...options, headers });
              if (response.status !== 401) return response;
              writeToken(null);
              const data = await response.json().catch(() => null);
              reason.textContent = data?.error ?? "Sign in to continue.";
              if (!overlay.isConnected) document.body.append(overlay);
              overlay.hidden = false;
              await new Promise(resolve => waiting.push(resolve));
            }
          };
        })();
        """;
}
