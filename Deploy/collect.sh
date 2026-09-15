#!/usr/bin/env bash
# Gathers everything a Lane install owns that git does not carry — secrets, the live
# configuration, the memory database, the prompt templates — into Deploy/data, ready to
# copy to another machine and hand to install.sh.
#
# Re-runnable: run it again before a transfer to pick up the current database.
#
# macOS and Linux, bash 3.2 or newer.

set -euo pipefail

BUNDLE="$(cd "$(dirname "$0")" && pwd -P)"
SOURCE="$(cd "$BUNDLE/.." && pwd -P)"
DATA="$BUNDLE/data"
DB=""

usage() {
    cat <<'USAGE'
Usage: collect.sh [--source DIR] [--db PATH] [--out DIR]

  --source DIR  Repository root to collect from (default: the directory above this one)
  --db PATH     Memory database to copy (default: the newest lane.db under Lane.Host/bin)
  --out DIR     Where to write the bundle (default: Deploy/data)
  -h, --help    This message
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --source) SOURCE="$2"; shift 2 ;;
        --db)     DB="$2";     shift 2 ;;
        --out)    DATA="$2";   shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

say()  { printf '  %s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

[ -d "$SOURCE/Lane.Host" ] || die "$SOURCE does not look like the Lane repository (no Lane.Host)."

mkdir -p "$DATA"

printf 'Collecting from %s\n' "$SOURCE"

# --- Secrets ------------------------------------------------------------------
# Stored without the leading dot: a bundle you can see the contents of with plain ls is
# harder to lose track of. install.sh puts them back as .env.

if [ -f "$SOURCE/.env" ]; then
    cp -p "$SOURCE/.env" "$DATA/env"
    chmod 600 "$DATA/env"
    say "env                    <- .env"
else
    warn "no .env in $SOURCE — Lane will not have any API keys on the new machine."
fi

if [ -f "$SOURCE/LaneCompanion/.env" ]; then
    cp -p "$SOURCE/LaneCompanion/.env" "$DATA/companion.env"
    chmod 600 "$DATA/companion.env"
    say "companion.env          <- LaneCompanion/.env"
fi

# --- Configuration ------------------------------------------------------------

cp -p "$SOURCE/Lane.Host/appsettings.json" "$DATA/appsettings.json"
say "appsettings.json       <- Lane.Host/appsettings.json"

if [ -f "$SOURCE/Lane.Host/appsettings.local.json" ]; then
    cp -p "$SOURCE/Lane.Host/appsettings.local.json" "$DATA/appsettings.local.json"
    say "appsettings.local.json <- Lane.Host/appsettings.local.json"
fi

# --- Prompts ------------------------------------------------------------------
# Committed, but they are the persona: a tuned Persona.md is data, not source, and a
# transfer that silently reverts it is the kind of loss you notice a week later.

rm -rf "$DATA/Prompts"
mkdir -p "$DATA/Prompts"
cp -p "$SOURCE"/Lane.Host/Prompts/*.md "$DATA/Prompts/"
say "Prompts/               <- Lane.Host/Prompts ($(ls -1 "$DATA/Prompts" | wc -l | tr -d ' ') templates)"

# --- Memory database ----------------------------------------------------------
# Whatever Lane remembers — sessions, profiles, notes, book positions, node identities,
# credits, sponsorships, the forum — is all in here.

if [ -z "$DB" ]; then
    for candidate in \
        "$SOURCE/Lane.Host/bin/Release/net10.0/lane.db" \
        "$SOURCE/Lane.Host/bin/Debug/net10.0/lane.db"
    do
        if [ -f "$candidate" ]; then
            if [ -z "$DB" ] || [ "$candidate" -nt "$DB" ]; then DB="$candidate"; fi
        fi
    done
fi

if [ -n "$DB" ] && [ -f "$DB" ]; then
    # .backup takes a consistent snapshot even while Lane is running and folds in the
    # write-ahead log; a plain cp of a live database can land mid-transaction.
    if command -v sqlite3 >/dev/null 2>&1; then
        rm -f "$DATA/lane.db"
        sqlite3 "$DB" ".backup '$DATA/lane.db'"
        say "lane.db                <- $DB (sqlite3 snapshot, $(wc -c < "$DATA/lane.db" | tr -d ' ') bytes)"

        # Every table the schema declares, so tables added by new features are checked too.
        SCHEMA="$SOURCE/Lane.Memory/Sqlite/LaneDatabase.cs"
        if [ -f "$SCHEMA" ]; then
            MISSING_TABLES=""
            for table in $(sed -n 's/.*CREATE TABLE IF NOT EXISTS \([A-Za-z_][A-Za-z0-9_]*\).*/\1/p' "$SCHEMA"); do
                rows="$(sqlite3 "$DATA/lane.db" "SELECT count(*) FROM $table" 2>/dev/null)" || {
                    MISSING_TABLES="$MISSING_TABLES $table"
                    continue
                }
                say "  $(printf '%-22s' "$table") $rows rows"
            done
            if [ -n "$MISSING_TABLES" ]; then
                warn "$DB lacks:$MISSING_TABLES — start the current build once so it creates them, then collect again."
            fi
        fi
    else
        cp -p "$DB" "$DATA/lane.db"
        if [ -f "$DB-wal" ]; then cp -p "$DB-wal" "$DATA/lane.db-wal"; fi
        if [ -f "$DB-shm" ]; then cp -p "$DB-shm" "$DATA/lane.db-shm"; fi
        say "lane.db                <- $DB (file copy, $(wc -c < "$DATA/lane.db" | tr -d ' ') bytes)"
        warn "sqlite3 not installed — copy taken with Lane stopped is the safe one."
    fi
else
    warn "no lane.db found; the new machine will start with an empty memory."
fi

# --- Manifest -----------------------------------------------------------------

{
    printf 'Lane data bundle\n'
    printf 'collected %s from %s on %s\n\n' "$(date '+%Y-%m-%d %H:%M:%S %z')" "$SOURCE" "$(uname -n)"
    find "$DATA" -type f ! -name MANIFEST.txt | sed "s|^$DATA/||" | sort | while read -r file; do
        printf '  %-24s %10s bytes\n' "$file" "$(wc -c < "$DATA/$file" | tr -d ' ')"
    done
} > "$DATA/MANIFEST.txt"

printf '\nBundle: %s\n' "$DATA"
printf 'It holds API keys — treat it like a password file, and do not commit it.\n'
printf 'Transfer with:  tar czf lane-data.tgz -C %s .\n' "$BUNDLE"
