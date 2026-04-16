# Alice
Alice is a discord bot for mediating certain interactions with Lane. 
Currently, they provide the ability to keep track of chess
games which can be played with Lane, utilizing the following commands:

- `%new_game username`
    * Creates a new game of chess against the person specified in `username`, and replies with the ID of the newly created game
- `%move game_id notation`
    * Makes the move specified in algebraic notation in `notation` in the game with ID `game_id`
- `%view_board game_id`
    * Gives an ASCII representation of the board of the game specified by `game_id`
- `%view_move game_id`
    * Gives a list of the moves made in the game specified by `game_id` so far
- `%view_fen game_id`
    * Gives the state of the board of the game specified by `game_id`

To use, you need to create a `.env` file in the directory of the binary which defines
`DISCORD_API_KEY`.