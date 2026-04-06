namespace Wizard.Head.Mouths
{
    public interface IMouth
    {
        public IAsyncEnumerable<byte[]> Speak(string text);
    }
}
