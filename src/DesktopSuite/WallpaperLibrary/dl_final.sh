#!/bin/bash
UA="Mozilla/5.0 (Windows NT 10.0; Win64; x64) WallpaperDownloader/1.0"
LOG="dl.log"
echo "START $(date)" > "$LOG"
JOBS=(
"清晨|web-dawn-1.webm|7046837|https://upload.wikimedia.org/wikipedia/commons/f/f1/Sunrise_Timelapse_%2830174294051%29.webm"
"中午|web-midday-1.webm|62031470|https://upload.wikimedia.org/wikipedia/commons/c/c6/Cumulus_timelapse_Skupowo-170324.webm"
"黄昏|web-sunset-1.webm|20582492|https://upload.wikimedia.org/wikipedia/commons/e/e9/2020-04-01_timelapse-sunset-Belfort.webm"
"晚上|web-night-city-1.webm|134521389|https://upload.wikimedia.org/wikipedia/commons/7/79/First_nights_in_Tokyo.webm"
"深夜|web-milkyway-1.webm|576647086|https://upload.wikimedia.org/wikipedia/commons/8/80/JAPAN_Milk_Way_4K_-_Beautiful_Star_and_Sky_at_Night_Time_Lapse.webm"
)
for job in "${JOBS[@]}"; do
  IFS='|' read -r folder name exp url <<< "$job"
  dest="$folder/动态壁纸/$name"
  tmp=".tmp_$name"
  if [ -f "$dest" ]; then echo "SKIP exists: $dest" | tee -a "$LOG"; continue; fi
  echo "=== $dest (expect $exp) ===" | tee -a "$LOG"
  ok=0
  for attempt in 1 2 3 4 5 6; do
    code=$(curl -sL --max-time 600 -A "$UA" -w "%{http_code}" -o "$tmp" "$url")
    sz=$(wc -c < "$tmp" 2>/dev/null || echo 0)
    echo "  attempt $attempt: http=$code size=$sz" | tee -a "$LOG"
    if [ "$code" = "200" ] && [ "$sz" -ge 204800 ]; then
      diff=$(( sz > exp ? sz - exp : exp - sz ))
      magic=$(head -c 4 "$tmp")
      if [ "$diff" -le 200000 ] && [ "$magic" = "$(printf '\x1a\x45\xdf\xa3')" ]; then
        mv "$tmp" "$dest"
        mb=$(awk "BEGIN{printf \"%.2f\", $sz/1048576}")
        echo "  OK -> $dest (${mb} MB) expect=$exp" | tee -a "$LOG"
        ok=1; break
      else
        echo "  size/format mismatch (diff=$diff); removing tmp" | tee -a "$LOG"
        rm -f "$tmp"
      fi
    else
      rm -f "$tmp"
    fi
    echo "  waiting 30s before retry" | tee -a "$LOG"
    sleep 30
  done
  if [ "$ok" -ne 1 ]; then echo "  FAILED: $dest" | tee -a "$LOG"; fi
  sleep 20
done
echo "ALL DONE $(date)" | tee -a "$LOG"
