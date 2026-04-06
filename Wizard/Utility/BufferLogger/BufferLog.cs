namespace Wizard.Utility.BufferLogger
{
    public sealed class BufferLog(int maxEntries)
    {
        readonly List<string> entries = [];

        public event Action<string>? EntryAdded;

        public void Add(string entry)
        {
            lock(entries)
            {
                entries.Add(entry);

                if(entries.Count > maxEntries) entries.RemoveAt(0);
            };

            EntryAdded?.Invoke(entry);
        }
    }
}