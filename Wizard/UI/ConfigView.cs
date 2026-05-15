using Terminal.Gui.Views;
using Wizard.Utility;

namespace Wizard.UI
{
    public sealed class ConfigView : FrameView
    {
        public ConfigView()
        {
            Title = "CONFIG";

            string responder, router, monologuer, summarizer, body, ear, mouth;


            responder  = Settings.instance?.LLMs.Respond   .Model ?? Program.DEFAULT_MODEL;
            router     = Settings.instance?.LLMs.Routing   .Model ?? Program.DEFAULT_MODEL;
            monologuer = Settings.instance?.LLMs.Monologue .Model ?? Program.DEFAULT_MODEL;
            summarizer = Settings.instance?.LLMs.Summarize?.Model ?? Program.DEFAULT_MODEL;

            body  = Settings.instance?.Body           ?? "Terminal";
            ear   = Settings.instance?.Hearing?.Ear   ?? "N/A";
            mouth = Settings.instance?.Speech ?.Mouth ?? "N/A";

            Label configLabel = new()
            {
                X = Y = 0,

                Text = $"""
RESPONDER:  {responder}
ROUTER:     {router}
MONOLOGUER: {monologuer}
SUMMARIZER: {summarizer}
BODY:       {body}
EAR:        {ear}
MOUTH:      {mouth}
"""
            };

            Add(configLabel);
        }
    }
}