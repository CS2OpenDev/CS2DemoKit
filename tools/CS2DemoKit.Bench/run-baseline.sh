#!/bin/bash
#
# Load-pipeline baseline. Publishes the bench tool, then runs it once per (round, demo) with a
# cooldown between runs, appending a CSV row per measurement.
#
#   ./run-baseline.sh [rounds] [out.csv]
#
# One process per measurement is the point: a fresh heap, no allocator history, and nothing
# carried between runs. Each process discards a full warm-up pipeline first, so no timed phase
# pays JIT.
#
# Runs under workstation GC, which is what a consumer gets unless their app opts into server GC.
# Server GC hides most of the collector cost, and the collector is where this pipeline's time is.
#
# The 1-minute load average is recorded with every row. A machine doing something else inflates
# every number here by a wide margin (Adobe Creative Cloud alone cost a factor of 2.2 once), and
# without the load column a contended sample is invisible after the fact. Discard rows whose load
# is out of line with the rest rather than averaging them in.
set -u

ROUNDS="${1:-10}"
OUT="${2:-baseline.csv}"
COOLDOWN="${COOLDOWN:-12}"

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DEMO_DIR="${DEMO_DIR:-$REPO/demos}"
BIN="$REPO/artifacts/bench"
LOG="${OUT%.csv}.log"

shopt -s nullglob
DEMOS=("$DEMO_DIR"/*.dem)
if [ ${#DEMOS[@]} -eq 0 ]; then
    echo "no .dem files under $DEMO_DIR" >&2
    exit 1
fi

dotnet publish "$REPO/tools/CS2DemoKit.Bench" -c Release -o "$BIN" --nologo -v q || exit 1

export DOTNET_gcServer=0
COMMIT="$(git -C "$REPO" rev-parse --short HEAD)"

echo "variant,demo,run,parse_ms,p1_ms,p2_ms,p3_ms,parse_pause_ms,parse_alloc_mb,retained_mb,gen0,gen1,gen2,build_ms,eval_ms,eval_pause_ms,eval_alloc_mb,frames,inner_messages,enum_ms,enum_alloc_mb,enum_pause_ms,walked,eval_gen0,eval_gen1,eval_gen2,load1" > "$OUT"
: > "$LOG"
echo "commit=$COMMIT rounds=$ROUNDS demos=${#DEMOS[@]} cooldown=${COOLDOWN}s" >> "$LOG"

total=$((ROUNDS * ${#DEMOS[@]}))
n=0
start=$(date +%s)

for round in $(seq 1 "$ROUNDS"); do
    for demo in "${DEMOS[@]}"; do
        n=$((n + 1))
        sleep "$COOLDOWN"
        load=$(uptime | sed 's/.*load averages*: //' | awk '{print $1}')
        line=$("$BIN/CS2DemoKit.Bench" "$demo" "$COMMIT" "$round" 2>>"$LOG")
        if [ $? -ne 0 ] || [ -z "$line" ]; then
            echo "FAILED run $n ($(basename "$demo") round $round)" >> "$LOG"
            continue
        fi
        echo "$line,$load" >> "$OUT"
        echo "[$n/$total] $(( $(date +%s) - start ))s load=$load $(basename "$demo") -> $(echo "$line" | cut -d, -f4,8,15,16)" >> "$LOG"
    done
done

echo "DONE $n runs in $(( $(date +%s) - start ))s" >> "$LOG"
echo "wrote $OUT ($n rows)"
