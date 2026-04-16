using NetCord;
using NetCord.Gateway;
using Newtonsoft.Json.Linq;

namespace LaneCompanion
{
    public sealed class Discord
    {
        readonly GatewayClient client;
        readonly Chess         chess;

        public Discord()
        {
            client = new GatewayClient(
                new BotToken(DotNetEnv.Env.GetString("DISCORD_API_KEY")),
                new GatewayClientConfiguration
                {
                    Intents = GatewayIntents.AllNonPrivileged | GatewayIntents.MessageContent
                }
            );

            chess = new();

            if(JSONWriter.HasData()) chess.Deserialize((JArray) JSONWriter.ReadData());

            client.MessageCreate += OnMessageCreate;
        }

        public async ValueTask OnMessageCreate(Message message)
        {
            string[] lines = message.Content.Split("\n");

            foreach (string line in lines)
            {
                if (!(line.Length > 0 && line[0] == '%')) continue; // not bot command

                string[] commandParts = line.TrimStart('%').Split(" ");

                switch (commandParts[0])
                {
                    case "new_game":
                        await CreateNewGame(commandParts, message);
                        break;

                    case "move":
                        await Move(commandParts, message);
                        break;

                    case "view_board":
                        await ViewBoard(commandParts, message);
                        break;

                    case "view_moves":
                        await ViewMoves(commandParts, message);
                        break;
                    
                    case "view_fen":
                        await ViewFen(commandParts, message);
                        break;

                    default:
                        await TellInvalid("Unknown command: " + commandParts[0], message);
                        break;
                }
            }
        }

        async Task CreateNewGame(string[] commandParts, Message message)
        {
            // %new_game against
            if(commandParts.Length != 2)
            {
                await TellInvalid("Invalid number of arguments, should provide competitor", message);
                return;
            }

            string white = message.Author.Username;
            string black = commandParts[1];

            int game = chess.CreateGame(white, black);

            await Say($"Created game {game} between {white} and {black}", message);
        }

        async Task Move(string[] commandParts, Message message)
        {
            if(commandParts.Length != 3)
            {
                await TellInvalid("Invalid number of arguments, should provide game number and move", message);
                return;
            }

            if(!int.TryParse(commandParts[1], out int game))
            {
                await TellInvalid("Game number was not integer", message);
                return;
            }

            string move = commandParts[2];

            Chess.MoveResult result = chess.PlayMove(message.Author.Username, game, move);

            switch (result)
            {
                case Chess.MoveResult.Succeeded:
                    await Say(
                        "Made move " + move + "\n" +
                        "```\n" + chess.GetBoardASCII(game) + "\n```\n" +
                        chess.GetFen(game), 
                        message
                    );
                    break;

                case Chess.MoveResult.InvalidMove:
                    await Say($"Move {move} is invalid", message);
                    break;

                case Chess.MoveResult.NotTurn:
                    await Say("It's not your turn", message);
                    break;

                case Chess.MoveResult.NoGame:
                    await Say($"Game {game} does not exist", message);
                    break;

                case Chess.MoveResult.Checkmate:
                    await Say($"Game won!", message);
                    break;

                case Chess.MoveResult.Stalemate:
                    await Say($"Stalemate!", message);
                    break;

                case Chess.MoveResult.Ended:
                    await Say($"Game ended for some reason or other!", message);
                    break;
                
                default:
                    await Say("Unknown result occurred", message);
                    break;
            }

            JSONWriter.WriteData(chess.Serialize());
        }

        async Task ViewBoard(string[] commandParts, Message message)
        {
            if(commandParts.Length != 2)
            {
                await TellInvalid("Invalid number of arguments, should provide game", message);
                return;
            }

            if(!int.TryParse(commandParts[1], out int game))
            {
                await TellInvalid("Game number was not integer", message);
                return;
            }

            if (!chess.GameExists(game))
            {
                await TellInvalid($"Game {game} does not exist", message);
                return;
            }

            await Say("```\n" + chess.GetBoardASCII(game) + "\n```", message);
        }

        async Task ViewMoves(string[] commandParts, Message message)
        {
            if(commandParts.Length != 2)
            {
                await TellInvalid("Invalid number of arguments, should provide game", message);
                return;
            }

            if(!int.TryParse(commandParts[1], out int game))
            {
                await TellInvalid("Game number was not integer", message);
                return;
            }

            if (!chess.GameExists(game))
            {
                await TellInvalid($"Game {game} does not exist", message);
                return;
            }

            await Say(chess.GetMoves(game), message);
        }

        async Task ViewFen(string[] commandParts, Message message)
        {
            if(commandParts.Length != 2)
            {
                await TellInvalid("Invalid number of arguments, should provide game", message);
                return;
            }

            if(!int.TryParse(commandParts[1], out int game))
            {
                await TellInvalid("Game number was not integer", message);
                return;
            }

            if (!chess.GameExists(game))
            {
                await TellInvalid($"Game {game} does not exist", message);
                return;
            }

            await Say(chess.GetMoves(game), message);
        }

        private static async Task TellInvalid(string text, Message message)
        {
            await message.ReplyAsync("Error: " + text);
        }

        private static async Task Say(string text, Message message)
        {
            await message.ReplyAsync(text);
        }

        public async Task ConnectAsync()
        {
            await client.StartAsync();
        }
    }
}