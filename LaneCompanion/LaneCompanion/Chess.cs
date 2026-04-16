using Chess;
using Newtonsoft.Json.Linq;

namespace LaneCompanion
{
    public sealed class Chess
    {
        readonly Dictionary<int, GameData> games = [];

        int bump = 0;

        public int CreateGame(string white, string black)
        {
            bump++;

            games[bump] = new(white, black, new());

            return bump;
        }

        public MoveResult PlayMove(string player, int game, string move)
        {
            if(!games.TryGetValue(game, out GameData data)) return MoveResult.NoGame;

            if (data.board.Turn == PieceColor.White && player != data.white) return MoveResult.NotTurn;
            if (data.board.Turn == PieceColor.Black && player != data.black) return MoveResult.NotTurn;

            try
            {
                if(data.board.Move(move)) 
                {
                    if(data.board.IsEndGame)
                    {
                        if(data.board.EndGame is null) return MoveResult.Ended;

                        return data.board.EndGame.EndgameType switch
                        {
                            EndgameType.Checkmate => MoveResult.Checkmate,
                            EndgameType.Stalemate => MoveResult.Stalemate,
                            _                     => MoveResult.Ended
                        };
                    }

                    return MoveResult.Succeeded;
                }
            } catch(Exception exception)
            {
                Console.WriteLine(exception.ToString());
            }

            return MoveResult.InvalidMove;
        }

        public bool GameExists(int game) => games.ContainsKey(game);

        public string GetBoardASCII(int game) => games[game].board.ToAscii();
        public string GetMoves     (int game) => games[game].board.ToPgn();
        public string GetFen       (int game) => games[game].board.ToFen();

        public JArray Serialize()
        {
            JArray serialized = [];

            foreach (KeyValuePair<int, GameData> pair in games)
            {
                JObject serializedGame = new()
                {
                    ["id"]    = pair.Key,
                    ["white"] = pair.Value.white,
                    ["black"] = pair.Value.black,
                    ["board"] = pair.Value.board.ToFen()
                };

                serialized.Add(serializedGame);
            }

            return serialized;
        }

        public void Deserialize(JArray data)
        {
            int largestID = 0;

            foreach (JObject game in data.Cast<JObject>())
            {
                int id = (int?) game["id"] ?? throw new Exception("ID was not integer");

                string white = (string?) game["white"] ?? throw new Exception("White was not string");
                string black = (string?) game["black"] ?? throw new Exception("Black was not string");
                string board = (string?) game["board"] ?? throw new Exception("Board was not string");

                games[id] = new(
                    white,
                    black,
                    ChessBoard.LoadFromFen(board)
                );

                if(id > largestID) largestID = id;
            }

            bump = largestID;
        }

        public enum MoveResult
        {
            Succeeded, InvalidMove, NotTurn, NoGame, Checkmate, Stalemate, Ended
        }

        readonly struct GameData(string white, string black, ChessBoard board)
        {
            public readonly string     white = white;
            public readonly string     black = black;
            public readonly ChessBoard board = board;
        }
    }
}