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

            if (Settings.instance is null)
            {
                llm   = "Claude";
                body  = "Terminal";
                ear   = "N/A";
                mouth = "N/A";
            }
            else
            {
                llm   = Settings.instance.LLM;
                body  = Settings.instance.Body;
                ear   = Settings.instance.Hearing is null ? "N/A" : Settings.instance.Hearing.Ear;
                mouth = Settings.instance.Speech  is null ? "N/A" : Settings.instance.Speech.Mouth;
            }

            Label configLabel = new()
            {
                X = Y = 0,

                Text = $"LLM:   {llm} \nBODY:  {body}\nEAR:   {ear}\nMOUTH: {mouth}"
            };

            Add(configLabel);
        }
    }
}