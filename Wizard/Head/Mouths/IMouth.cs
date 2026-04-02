namespace Wizard.Head.Mouths
{
    public interface IMouth
    {
        public Task<byte[]> Speak(string text);
    }
}