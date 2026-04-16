using System.Reflection;
using Newtonsoft.Json.Linq;

namespace LaneCompanion
{
    public static class JSONWriter
    {
        static readonly string dataPath;

        static JSONWriter() 
        {
            string? possiblePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) 
                                ?? throw new Exception("Somehow the path of the executing assembly is null");
            
            dataPath = Path.Join(possiblePath, "data.json");
        }

        public static void WriteData(JToken data)
        {
            File.WriteAllText(dataPath, data.ToString());
        }

        public static bool HasData()
        {
            return Path.Exists(dataPath);
        }

        public static JToken ReadData()
        {
            string data = File.ReadAllText(dataPath);

            return JToken.Parse(data);
        }
    }
}