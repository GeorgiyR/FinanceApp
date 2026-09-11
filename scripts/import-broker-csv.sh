#!/usr/bin/env bash
# =============================================================================
# import-broker-csv.sh
# Broker CSV → broker_csv_staging importer
#
# USAGE:
#   bash scripts/import-broker-csv.sh [OPTIONS] FILE [FILE ...]
#
# OPTIONS:
#   -h HOST      MariaDB host          (default: 127.0.0.1)
#   -P PORT      MariaDB port          (default: 3306)
#   -u USER      MariaDB user          (default: financeapp)
#   -d DATABASE  Target database       (default: financeapp)
#   --truncate   TRUNCATE staging table before importing (fresh run)
#   --dry-run    Print generated INSERT statements; do NOT execute them
#
# CREDENTIALS:
#   Supply the password via the MYSQL_PWD environment variable or a
#   ~/.my.cnf [client] section.  Never pass passwords on the command line.
#
# ENCODING:
#   Broker exports are often Windows-1252 / ISO-8859-1.  The script
#   detects non-UTF-8 bytes in each file and converts via iconv when needed.
#   Requires: iconv, file (libmagic), mysql client.
#
# EXIT CODES:
#   0  – success
#   1  – usage / missing dependency
#   2  – one or more files had parse errors (rows inserted with ParseError set)
#   3  – database error
# =============================================================================

set -euo pipefail

# ---------------------------------------------------------------------------
# Defaults
# ---------------------------------------------------------------------------
DB_HOST="127.0.0.1"
DB_PORT="3306"
DB_USER="financeapp"
DB_NAME="financeapp"
DO_TRUNCATE=0
DRY_RUN=0
CSV_FILES=()

# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------
while [[ $# -gt 0 ]]; do
    case "$1" in
        -h) DB_HOST="$2"; shift 2 ;;
        -P) DB_PORT="$2"; shift 2 ;;
        -u) DB_USER="$2"; shift 2 ;;
        -d) DB_NAME="$2"; shift 2 ;;
        --truncate) DO_TRUNCATE=1; shift ;;
        --dry-run)  DRY_RUN=1;     shift ;;
        --) shift; CSV_FILES+=("$@"); break ;;
        -*) echo "Unknown option: $1" >&2; exit 1 ;;
        *)  CSV_FILES+=("$1"); shift ;;
    esac
done

if [[ ${#CSV_FILES[@]} -eq 0 ]]; then
    echo "Usage: $0 [OPTIONS] FILE [FILE ...]" >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# Dependency checks
# ---------------------------------------------------------------------------
for cmd in iconv mysql awk sed; do
    if ! command -v "$cmd" &>/dev/null; then
        echo "Required command not found: $cmd" >&2
        exit 1
    fi
done

MYSQL_OPTS=(-h "$DB_HOST" -P "$DB_PORT" -u "$DB_USER" "$DB_NAME"
            --default-character-set=utf8mb4 --batch --silent)

# ---------------------------------------------------------------------------
# Helper: run SQL (dry-run aware)
# ---------------------------------------------------------------------------
run_sql() {
    if [[ $DRY_RUN -eq 1 ]]; then
        echo "-- [DRY RUN SQL] --"
        echo "$1"
        echo "-- [END DRY RUN SQL] --"
    else
        mysql "${MYSQL_OPTS[@]}" -e "$1"
    fi
}

# ---------------------------------------------------------------------------
# Helper: escape a single-quoted SQL string value
# ---------------------------------------------------------------------------
sql_escape() {
    # Replace \ with \\, then ' with \'
    printf '%s' "$1" | sed "s/\\\\/\\\\\\\\/g; s/'/\\\\'/g"
}

# ---------------------------------------------------------------------------
# Helper: parse German number to decimal string
#   Input: "1.234,56"  Output: "1234.56"
#   Returns empty string if input is not a valid German number.
# ---------------------------------------------------------------------------
parse_german_number() {
    local raw="$1"
    # Remove thousands separators (.), replace decimal comma with dot
    local norm
    norm=$(printf '%s' "$raw" | sed 's/\.//g; s/,/./')
    # Validate: optional leading minus, digits, optional dot+digits
    if printf '%s' "$norm" | grep -Eq '^-?[0-9]+(\.[0-9]+)?$'; then
        printf '%s' "$norm"
    else
        printf ''
    fi
}

# ---------------------------------------------------------------------------
# Helper: parse DD.MM.YYYY to YYYY-MM-DD
# ---------------------------------------------------------------------------
parse_date() {
    local raw="$1"
    if printf '%s' "$raw" | grep -Eq '^[0-9]{2}\.[0-9]{2}\.[0-9]{4}$'; then
        printf '%s-%s-%s' "${raw:6:4}" "${raw:3:2}" "${raw:0:2}"
    else
        printf ''
    fi
}

# ---------------------------------------------------------------------------
# Helper: validate ISIN (12 uppercase alphanumeric chars, first 2 alpha)
# ---------------------------------------------------------------------------
validate_isin() {
    local raw
    raw=$(printf '%s' "$1" | tr '[:lower:]' '[:upper:]' | tr -d ' ')
    if printf '%s' "$raw" | grep -Eq '^[A-Z]{2}[A-Z0-9]{10}$'; then
        printf '%s' "$raw"
    else
        printf ''
    fi
}

# ---------------------------------------------------------------------------
# Helper: derive TradeType and BrokerRef from Buchungsinformation
# ---------------------------------------------------------------------------
parse_buchungsinformation() {
    local info="$1"
    # Normalise encoding artefacts for keyword matching
    local norm
    norm=$(printf '%s' "$info" | sed 's/Ausf.hrung/Ausführung/g; s/Verh.ltnis/Verhältnis/g')

    TRADE_TYPE="Unknown"
    BROKER_REF=""
    CORP_HINT=""

    # Corporate actions (check before Kauf/Verkauf)
    if printf '%s' "$norm" | grep -qi 'Split im Verh'; then
        TRADE_TYPE="CorporateAction"
        CORP_HINT=$(printf '%s' "$norm" | grep -oi 'Split im Verh[äa]ltnis[^;]*' | head -1 || true)
        return
    fi
    if printf '%s' "$norm" | grep -qi 'Kapitalerhöhung\|Kapitalerh.hung'; then
        TRADE_TYPE="CorporateAction"
        CORP_HINT=$(printf '%s' "$norm" | grep -oi 'Kapitalerh[öo]hung[^;]*' | head -1 || true)
        return
    fi
    if printf '%s' "$norm" | grep -qi 'Lagerstellenwechsel'; then
        TRADE_TYPE="CorporateAction"
        CORP_HINT="Lagerstellenwechsel"
        return
    fi

    # Ordinary trades (check Verkauf first: it contains 'Kauf' as a substring)
    if printf '%s' "$norm" | grep -qiE '\bVerkauf\b'; then
        TRADE_TYPE="Sell"
    elif printf '%s' "$norm" | grep -qiE '\bKauf\b'; then
        TRADE_TYPE="Buy"
    fi

    # BrokerRef: last whitespace-separated token that is purely numeric
    # e.g. "Ausführung ORDER Kauf IE00BKVD2N49 315712787" → 315712787
    BROKER_REF=$(printf '%s' "$info" | awk '{print $NF}' | grep -E '^[0-9]+$' || true)
}

# ---------------------------------------------------------------------------
# Optional: truncate staging table
# ---------------------------------------------------------------------------
if [[ $DO_TRUNCATE -eq 1 ]]; then
    echo "=== Truncating broker_csv_staging ==="
    run_sql "TRUNCATE TABLE \`broker_csv_staging\`;"
fi

# ---------------------------------------------------------------------------
# Process each CSV file
# ---------------------------------------------------------------------------
TOTAL_ROWS=0
TOTAL_ERRORS=0
EXIT_CODE=0

for CSV_FILE in "${CSV_FILES[@]}"; do
    if [[ ! -f "$CSV_FILE" ]]; then
        echo "File not found: $CSV_FILE" >&2
        EXIT_CODE=2
        continue
    fi

    BASENAME=$(basename "$CSV_FILE")
    echo "=== Processing: $BASENAME ==="

    # -------------------------------------------------------------------------
    # Encoding detection and normalisation
    # -------------------------------------------------------------------------
    WORK_FILE="$CSV_FILE"
    TMP_FILE=""

    # Try to detect encoding; fall back to binary check
    if command -v file &>/dev/null; then
        FILE_ENC=$(file -b --mime-encoding "$CSV_FILE" 2>/dev/null || true)
    else
        FILE_ENC=""
    fi

    # Check for non-UTF-8 bytes (quick heuristic)
    if ! iconv -f utf-8 -t utf-8 "$CSV_FILE" >/dev/null 2>&1; then
        echo "  Detected non-UTF-8 encoding ($FILE_ENC); converting from ISO-8859-1 ..."
        TMP_FILE=$(mktemp /tmp/broker_csv_XXXXXX.csv)
        iconv -f ISO-8859-1 -t UTF-8 "$CSV_FILE" > "$TMP_FILE"
        WORK_FILE="$TMP_FILE"
    else
        echo "  Encoding: UTF-8 (no conversion needed)"
    fi

    # -------------------------------------------------------------------------
    # Parse CSV rows
    # -------------------------------------------------------------------------
    # Expected header:
    # Buchungstag;Valuta;Bezeichnung;ISIN;Nominal (Stk.);;Betrag;;Kurs;;Devisenkurs;TA.-Nr.;Buchungsinformation
    # Column indices (0-based):
    #  0  Buchungstag
    #  1  Valuta
    #  2  Bezeichnung
    #  3  ISIN
    #  4  Nominal (Stk.)
    #  5  Unit (Stück / blank)
    #  6  Betrag
    #  7  BetragCurrency
    #  8  Kurs
    #  9  KursCurrency
    # 10  Devisenkurs
    # 11  TA.-Nr.
    # 12  Buchungsinformation

    ROW_NUM=0
    DATA_ROWS=0
    HEADER_SEEN=0

    while IFS=';' read -r -a FIELDS; do
        ROW_NUM=$((ROW_NUM + 1))

        # Skip header row (first row)
        if [[ $HEADER_SEEN -eq 0 ]]; then
            HEADER_SEEN=1
            continue
        fi

        # Skip blank lines
        if [[ ${#FIELDS[@]} -lt 3 ]]; then
            continue
        fi

        # Pad to at least 13 fields
        while [[ ${#FIELDS[@]} -lt 13 ]]; do
            FIELDS+=("")
        done

        RAW_BUCHUNGSTAG="${FIELDS[0]}"
        RAW_VALUTA="${FIELDS[1]}"
        BEZEICHNUNG="${FIELDS[2]}"
        RAW_ISIN="${FIELDS[3]}"
        RAW_NOMINAL="${FIELDS[4]}"
        NOMINAL_UNIT="${FIELDS[5]}"
        RAW_BETRAG="${FIELDS[6]}"
        BETRAG_CURRENCY="${FIELDS[7]}"
        RAW_KURS="${FIELDS[8]}"
        KURS_CURRENCY="${FIELDS[9]}"
        RAW_DEVISENKURS="${FIELDS[10]}"
        TA_NR="${FIELDS[11]}"
        BUCHUNGSINFORMATION="${FIELDS[12]}"

        # ----- Normalise / parse -----
        PARSE_ERROR=""

        BUCHUNGSTAG=$(parse_date "$RAW_BUCHUNGSTAG")
        if [[ -z "$BUCHUNGSTAG" && -n "$RAW_BUCHUNGSTAG" ]]; then
            PARSE_ERROR="${PARSE_ERROR}Invalid Buchungstag='${RAW_BUCHUNGSTAG}'; "
        fi

        VALUTA=$(parse_date "$RAW_VALUTA")
        if [[ -z "$VALUTA" && -n "$RAW_VALUTA" ]]; then
            PARSE_ERROR="${PARSE_ERROR}Invalid Valuta='${RAW_VALUTA}'; "
        fi

        ISIN=$(validate_isin "$RAW_ISIN")
        if [[ -z "$ISIN" && -n "$RAW_ISIN" ]]; then
            PARSE_ERROR="${PARSE_ERROR}Invalid ISIN='${RAW_ISIN}'; "
        fi

        NOMINAL_SIGNED_STR=$(parse_german_number "$RAW_NOMINAL")
        if [[ -z "$NOMINAL_SIGNED_STR" && -n "$RAW_NOMINAL" ]]; then
            PARSE_ERROR="${PARSE_ERROR}Invalid Nominal='${RAW_NOMINAL}'; "
            NOMINAL_SQL="NULL"
            NOMINAL_SIGNED_SQL="NULL"
        else
            # ABS value for Nominal
            NOMINAL_ABS=$(printf '%s' "$NOMINAL_SIGNED_STR" | sed 's/^-//')
            NOMINAL_SQL="'$(sql_escape "$NOMINAL_ABS")'"
            NOMINAL_SIGNED_SQL="'$(sql_escape "$NOMINAL_SIGNED_STR")'"
        fi

        BETRAG_STR=$(parse_german_number "$RAW_BETRAG")
        if [[ -z "$BETRAG_STR" && -n "$RAW_BETRAG" ]]; then
            PARSE_ERROR="${PARSE_ERROR}Invalid Betrag='${RAW_BETRAG}'; "
            BETRAG_SQL="NULL"
        else
            BETRAG_ABS=$(printf '%s' "$BETRAG_STR" | sed 's/^-//')
            BETRAG_SQL="'$(sql_escape "$BETRAG_ABS")'"
        fi

        KURS_STR=$(parse_german_number "$RAW_KURS")
        if [[ -z "$KURS_STR" && -n "$RAW_KURS" ]]; then
            PARSE_ERROR="${PARSE_ERROR}Invalid Kurs='${RAW_KURS}'; "
            KURS_SQL="NULL"
        else
            KURS_ABS=$(printf '%s' "$KURS_STR" | sed 's/^-//')
            KURS_SQL="'$(sql_escape "$KURS_ABS")'"
        fi

        DEVISENKURS_STR=$(parse_german_number "$RAW_DEVISENKURS")
        if [[ -z "$DEVISENKURS_STR" && -n "$RAW_DEVISENKURS" ]]; then
            PARSE_ERROR="${PARSE_ERROR}Invalid Devisenkurs='${RAW_DEVISENKURS}'; "
            DEVISENKURS_SQL="NULL"
        else
            DEVISENKURS_SQL="'$(sql_escape "$DEVISENKURS_STR")'"
        fi

        # Trade type + BrokerRef
        parse_buchungsinformation "$BUCHUNGSINFORMATION"
        # Variables set: TRADE_TYPE, BROKER_REF, CORP_HINT

        # Null-safe SQL values
        NULL_OR() {
            if [[ -n "$1" ]]; then echo "'$(sql_escape "$1")'"; else echo "NULL"; fi
        }

        SQL_STMT="INSERT INTO \`broker_csv_staging\`
  (\`SourceFile\`,\`SourceRow\`,
   \`RawBuchungstag\`,\`RawValuta\`,\`Bezeichnung\`,\`RawISIN\`,
   \`RawNominal\`,\`NominalUnit\`,\`RawBetrag\`,\`BetragCurrency\`,
   \`RawKurs\`,\`KursCurrency\`,\`RawDevisenkurs\`,\`TaNr\`,\`Buchungsinformation\`,
   \`Buchungstag\`,\`Valuta\`,\`ISIN\`,
   \`Nominal\`,\`NominalSigned\`,\`Betrag\`,\`Kurs\`,\`Devisenkurs\`,
   \`TradeType\`,\`BrokerRef\`,\`CorporateActionHint\`,\`ParseError\`,\`MatchStatus\`)
VALUES (
  '$(sql_escape "$BASENAME")', $ROW_NUM,
  $(NULL_OR "$RAW_BUCHUNGSTAG"), $(NULL_OR "$RAW_VALUTA"),
  $(NULL_OR "$BEZEICHNUNG"), $(NULL_OR "$RAW_ISIN"),
  $(NULL_OR "$RAW_NOMINAL"), $(NULL_OR "$NOMINAL_UNIT"),
  $(NULL_OR "$RAW_BETRAG"), $(NULL_OR "$BETRAG_CURRENCY"),
  $(NULL_OR "$RAW_KURS"), $(NULL_OR "$KURS_CURRENCY"),
  $(NULL_OR "$RAW_DEVISENKURS"), $(NULL_OR "$TA_NR"),
  $(NULL_OR "$BUCHUNGSINFORMATION"),
  $(NULL_OR "$BUCHUNGSTAG"), $(NULL_OR "$VALUTA"), $(NULL_OR "$ISIN"),
  $NOMINAL_SQL, $NOMINAL_SIGNED_SQL, $BETRAG_SQL, $KURS_SQL, $DEVISENKURS_SQL,
  '$(sql_escape "$TRADE_TYPE")', $(NULL_OR "$BROKER_REF"),
  $(NULL_OR "$CORP_HINT"),
  $(NULL_OR "$PARSE_ERROR"),
  $(if [[ -n "$PARSE_ERROR" ]]; then echo "'PARSE_ERROR'"; else echo "'PENDING'"; fi)
);"

        if [[ -n "$PARSE_ERROR" ]]; then
            echo "  Row $ROW_NUM: PARSE_ERROR – $PARSE_ERROR"
            TOTAL_ERRORS=$((TOTAL_ERRORS + 1))
        fi

        run_sql "$SQL_STMT"
        TOTAL_ROWS=$((TOTAL_ROWS + 1))
        DATA_ROWS=$((DATA_ROWS + 1))

    done < "$WORK_FILE"

    # Cleanup temp file
    if [[ -n "$TMP_FILE" ]]; then
        rm -f "$TMP_FILE"
    fi

    echo "  Inserted $DATA_ROWS data rows from $BASENAME"
done

echo ""
echo "=== Import complete: $TOTAL_ROWS rows total, $TOTAL_ERRORS parse errors ==="
if [[ $TOTAL_ERRORS -gt 0 ]]; then
    echo "Rows with parse errors were inserted with MatchStatus='PARSE_ERROR'."
    echo "Review them with:"
    echo "  SELECT * FROM \`broker_csv_staging\` WHERE \`MatchStatus\` = 'PARSE_ERROR';"
    EXIT_CODE=2
fi

exit $EXIT_CODE
