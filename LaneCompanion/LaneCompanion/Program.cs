namespace LaneCompanion
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            DotNetEnv.Env.TraversePath().Load();

            Discord discord = new();

            await discord.ConnectAsync();

            Console.WriteLine("Connected");

            await Task.Delay(-1);
        }
    }
}