using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Wizard.Body
{
    public sealed class EmoticonServer(int port, string defaultEmoticon)
    {
        readonly HttpListener                       listener = new();
        readonly ConcurrentDictionary<Guid, Stream> clients  = new();

        string currentEmoticon = defaultEmoticon;

        public void Start()
        {
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();

            _ = AcceptLoop();
        }

        public void SetEmoticon(string emoticon)
        {
            currentEmoticon = emoticon;
            _ = BroadcastAsync($"data: {emoticon}\n\n");
        }

        private async Task BroadcastAsync(string message)
        {
            byte[]      bytes    = Encoding.UTF8.GetBytes(message);
            List<Guid>  toRemove = [];

            foreach ((Guid id, Stream stream) in clients)
            {
                try
                {
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
                catch
                {
                    toRemove.Add(id);
                }
            }

            foreach (Guid id in toRemove) clients.TryRemove(id, out _);
        }

        private async Task AcceptLoop()
        {
            while (listener.IsListening)
            {
                try
                {
                    HttpListenerContext ctx = await listener.GetContextAsync();

                    _ = HandleRequest(ctx);
                }
                catch { }
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx)
        {
            if (ctx.Request.Url?.AbsolutePath == "/events")
            {
                ctx.Response.ContentType = "text/event-stream";

                ctx.Response.Headers.Add("Cache-Control",               "no-cache");
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");

                ctx.Response.SendChunked = true;

                Guid   id     = Guid.NewGuid();
                Stream stream = ctx.Response.OutputStream;

                clients[id] = stream;

                try
                {
                    // send current emoticon immediately on connect
                    byte[] initial = Encoding.UTF8.GetBytes($"data: {currentEmoticon}\n\n");

                    await stream.WriteAsync(initial);
                    await stream.FlushAsync();

                    while (true)
                    {
                        await Task.Delay(15000);

                        byte[] heartbeat = Encoding.UTF8.GetBytes(": ping\n\n");

                        await stream.WriteAsync(heartbeat);
                        await stream.FlushAsync();
                    }
                }
                catch { }
                finally
                {
                    clients.TryRemove(id, out _);
                    ctx.Response.Close();
                }
            }
            else
            {
                string pagePath = Path.Join(AppContext.BaseDirectory, "Body", "face.html");
                byte[] html     = await File.ReadAllBytesAsync(pagePath);

                ctx.Response.ContentType     = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = html.Length;

                await ctx.Response.OutputStream.WriteAsync(html);

                ctx.Response.Close();
            }
        }
    }
}
