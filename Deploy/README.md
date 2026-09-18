# Deploy

Moving Lane to another machine is two commands: `collect.sh` on the old one, `install.sh`
on the new one. The repository carries the code; this carries everything else.

## What counts as data

| File | Comes from | Why it has to travel |
|---|---|---|
| `data/env` | `.env` | Every API key. Not in git, and nothing works without it. |
| `data/appsettings.json` | `Lane.Host/appsettings.json` | Surfaces, models, channel ids, energy, voice — the tuned instance, not the committed default. |
| `data/appsettings.local.json` | same, if present | Local overrides, never committed. |
| `data/Prompts/` | `Lane.Host/Prompts/` | Persona, monologue, routing, summary, profile. Committed, but a tuned `Persona.md` is data. |
| `data/lane.db` | `Lane.Host/bin/*/net10.0/lane.db` | All of Lane's memory: sessions, transcripts, profiles, notes, book positions. |

Books (`Resources/Books`) and the source are in git, and the build copies them into the
output, so they are not bundled.

## Old machine

```sh
./Deploy/collect.sh                 # fills Deploy/data
tar czf lane-data.tgz -C Deploy .   # optional: one file to scp
```

Stop Lane first if you can. If `sqlite3` is installed, `collect.sh` takes a proper
snapshot and a live copy is safe anyway.

## Same machine, back into the checkout

To put the bundle back where it came from and build nothing — config and prompts into
`Lane.Host/`, `.env` at the repository root, `lane.db` into `Lane.Host/bin/Debug/net10.0/`
so `dotnet run` picks it up:

```sh
./Deploy/install.sh --in-place
```

It works before the first build too: the files sit in `bin/Debug/net10.0` and the build
fills in around them. Add `--release` to target the Release output instead.

## New machine

```sh
git clone <repo> lane-src
cd lane-src
tar xzf ~/lane-data.tgz -C Deploy   # if you moved the bundle as a tarball
./Deploy/install.sh --target /srv/lane
```

`install.sh` checks for a .NET 10 SDK, warns about missing optional audio tools, restores
`appsettings.json` and the prompts into the checkout (they are copied into the output on
every build, so they have to live there), publishes Release into the target, drops `.env`
in beside the binary at mode 0600, restores `lane.db` and integrity-checks it, and reports
any `env:` reference in the settings with no value behind it.

Anything it replaces is kept as `<file>.backup-<timestamp>` — unless the file already
matches, in which case there is nothing to keep. Prompts are backed up one at a time, so a
persona edited on the target survives as a copy. Re-running is safe.

Useful flags:

- `--in-place` — restore into the checkout and build nothing (see above)
- `--target DIR` — where Lane runs from (default `$LANE_HOME`, else `~/lane`)
- `--keep-db` — refresh the binaries and config, leave the memory on the target alone
- `--skip-build` — place every file, but do not publish; for a machine that already built
- `--service` — register and start Lane under systemd, or launchd on macOS
- `--debug` / `--release` — which configuration to build (Release by default, Debug under
  `--in-place`)

## Running

```sh
cd /srv/lane && ./lane            # dashboard
cd /srv/lane && ./lane --no-tui   # plain logs, for a service or a pipe
```

Paths resolve against the binary's directory, so `lane.db`, `Prompts/`, `Books/` and
`appsettings.json` all live next to `lane`. `.env` is found by walking up from the working
directory, so run Lane from the target directory (both service definitions set
`WorkingDirectory`).

`appsettings.json` binds the face (5050), the API (5080) and the dashboard (5090) on every
interface. To reach them from another network, open those ports in the host firewall and
forward them on the router, or put the host on a tailnet. The API requires a client key; the
face does not. The dashboard is open from loopback, and anywhere else requires signing in
with the security key whose node identity is `Dashboard:OwnerKeyId`, through the node app's
portal popup like the portal itself (so `Nodes:Enabled` must be on).

## Optional tools

Nothing here blocks startup; each one is warned about, not enforced.

- `flite` — the configured TTS provider (`Lane:Audio:Provider`)
- `ffmpeg` — only if `Lane:Audio:Microphone` is enabled
- `aplay` / `paplay` / `afplay` — local playback
- `sqlite3` — clean snapshots, integrity checks, poking at memory by hand

## Secrets

`Deploy/data` holds live API keys and every conversation Lane has had. `Deploy/.gitignore`
keeps it out of the repository; keep it out of anywhere else you would not put a password
file, and delete the tarball once the transfer is done.
