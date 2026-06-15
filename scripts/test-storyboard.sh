#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Storyboard generation test harness.
#
# Feeds a deliberately MINIMAL, human-like premise to /api/storyboard and prints
# how gpt-5-mini expands it into a per-clip film script — narration (voiceover) +
# on-screen dialogue — so you can judge:
#   * cinematic flow   (does the narration read as ONE continuous voiceover?)
#   * emotional subtext (is the dialogue indirect, not on-the-nose?)
#   * audio budget      (words/clip vs the ~10-word / 5s speakable target — the
#                        root cause of "audio and story not in line")
# in any language.
#
# Usage:
#   scripts/test-storyboard.sh                       # built-in English + Hindi presets (same premise)
#   scripts/test-storyboard.sh "<idea>" "<dialog hint>" "<Language>" [clipSeconds] [totalSeconds]
#
# Env:
#   BASE_URL   target server (default http://127.0.0.1:5099)
# ---------------------------------------------------------------------------
set -uo pipefail
BASE_URL="${BASE_URL:-http://127.0.0.1:5099}"

run_one() {
  local idea="$1" dialog="$2" lang="$3" clipSec="${4:-5}" total="${5:-20}"
  local slug; slug=$(printf '%s' "$lang" | tr '[:upper:] ' '[:lower:]_')
  local out="/tmp/sb_${slug}.json"
  local budget=$(( clipSec * 2 )); [ "$budget" -lt 6 ] && budget=6

  echo
  echo "============================================================"
  echo "LANGUAGE : $lang"
  echo "IDEA     : $idea"
  echo "DIALOG   : ${dialog:-<none — model invents the lines>}"
  echo "BUDGET   : ${clipSec}s/clip, ${total}s total  (~${budget} speakable words/clip)"
  echo "============================================================"

  local body
  body=$(jq -n --arg p "$idea" --arg d "$dialog" --arg l "$lang" \
    --argjson cs "$clipSec" --argjson ts "$total" \
    '{prompt:$p, dialogDirection:$d, language:$l, clipSeconds:$cs, totalSeconds:$ts, narrate:true, size:"832x480"}')

  local code
  code=$(curl -s -o "$out" -w "%{http_code}" -X POST "$BASE_URL/api/storyboard" \
    -H 'Content-Type: application/json' -d "$body" --max-time 150)
  if [ "$code" != "200" ]; then
    echo "HTTP $code — request failed:"; cat "$out"; echo; return 1
  fi

  jq -r '"TITLE   : \(.story.title)\nLOGLINE : \(.story.logline)\nCAST    : " + ([.story.cast[]?|.name]|join(", "))' "$out"
  echo
  jq -r '.story.clips[] |
    "── clip \(.index): \(.title)",
    "   NARR (\((.narration//"")|split(" ")|length)w): \(.narration//"")",
    "   DLG  (\(if ((.dialog//"")|length)>0 then ((.dialog)|split(" ")|length) else 0 end)w)\(if ((.dialog//"")|length)>0 then " ["+(.speaker//"?")+"]" else "" end): \(if ((.dialog//"")|length)>0 then .dialog else "—" end)"
  ' "$out"
  echo
  echo "── word-budget check (total spoken words per clip; target ~${budget}) ──"
  jq -r --argjson b "$budget" '
    [ .story.clips[] | (((.narration//"")|split(" ")|length) + (if ((.dialog//"")|length)>0 then ((.dialog)|split(" ")|length) else 0 end)) ] as $w |
    "   per clip: \($w|join(", "))",
    "   max \($w|max)  avg \(($w|add)/($w|length)|floor)  → over-budget clips: \([ $w[] | select(. > $b) ]|length)/\($w|length)"
  ' "$out"
  echo
  echo "── CONTINUOUS NARRATION (read end-to-end — should flow as one voiceover) ──"
  jq -r '[.story.clips[].narration//""]|join("  ")' "$out"
  echo
  echo "── DIALOGUE TRACK ──"
  jq -r '.story.clips[]|select(((.dialog//"")|length)>0)|"   \(.speaker//"?"): \(.dialog)"' "$out"
  echo
  echo "raw JSON saved: $out"
}

if [ "$#" -ge 3 ]; then
  run_one "$@"
else
  # Minimal, human-like premise — SAME idea in English then Hindi, to compare.
  run_one "old man feeds pigeons alone every morning" "he's waiting for someone who stopped coming" "English" 5 20
  run_one "old man feeds pigeons alone every morning" "he's waiting for someone who stopped coming" "Hindi"   5 20
fi
