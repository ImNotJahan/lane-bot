using Terminal.Gui.Views;
using Wizard.Utility;

namespace Wizard.UI
{
    public sealed class ConfigView : FrameView
    {
        public ConfigView()
        {
            Title = "CONFIG";

            string llm, body, ear, mouth;


            llm   = "hi";//Settings.instance.LLM;
            body  = Settings.instance?.Body ?? "Terminal";
            ear   = Settings.instance?.Hearing?.Ear   ?? "N/A";
            mouth = Settings.instance?.Speech ?.Mouth ?? "N/A";

            Label configLabel = new()
            {
                X = Y = 0,

                Text = $"LLM:   {llm} \nBODY:  {body}\nEAR:   {ear}\nMOUTH: {mouth}"
            };

            Add(configLabel);
        }
    }
}