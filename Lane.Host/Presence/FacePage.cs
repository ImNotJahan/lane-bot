using System.Net;

namespace Lane.Host.Presence;

/// <summary>
/// The face itself. Inlined rather than shipped as a file so the server has nothing to
/// find at runtime and no path to get wrong.
/// </summary>
internal static class FacePage
{
    public static string Html(string emoticon) =>
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>lane</title>
          <style>
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body {
              background: #0e0e0e;
              display: flex;
              align-items: center;
              justify-content: center;
              height: 100vh;
              font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
            }
            #face {
              color: #d4d4d4;
              font-size: 6rem;
              transition: opacity 0.25s ease;
              user-select: none;
              white-space: pre;
            }
          </style>
        </head>
        <body>
          <div id="face">{{WebUtility.HtmlEncode(emoticon)}}</div>
          <script>
            const el = document.getElementById('face');
            let source;

            function connect() {
              source = new EventSource('/events');

              source.onmessage = event => {
                el.style.opacity = 0;
                setTimeout(() => { el.textContent = event.data; el.style.opacity = 1; }, 250);
              };

              // Lane restarting should not leave a dead page behind.
              source.onerror = () => { source.close(); setTimeout(connect, 2000); };
            }

            connect();
          </script>
        </body>
        </html>
        """;
}
