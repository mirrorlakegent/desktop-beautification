#!/bin/bash
UA="Mozilla/5.0 (Windows NT 10.0; Win64; x64) WallpaperDownloader/1.0"
JOBS=(
"清晨|web-dawn-1.webm|https://upload.wikimedia.org/wikipedia/commons/f/f1/Sunrise_Timelapse_%2830174294051%29.webm"
"中午|web-midday-1.webm|https://upload.wikimedia.org/wikipedia/commons/c/c6/Cumulus_timelapse_Skupowo-170324.webm"
"黄昏|web-sunset-1.webm|https://upload.wikimedia.org/wikipedia/commons/e/e9/2020-04-01_timelapse-sunset-Belfort.webm"
"晚上|web-night-city-1.webm|https://upload.wikimedia.org/wikipedia/commons/7/79/First_nights_in_Tokyo.webm"
)
for job in "${JOBS[@]}"; do
  IFS='|' read -r folder name url <<< "$job"
  dest="$folder/动态壁纸/$name"
  tmp=".tmp_$name"
  if [ -f "$dest" ]; then echo "SKIP exists: $dest"; continue; fi
  echo "=== $dest ==="
  for attempt in 1 2 3 4; do
    code=$(curl -sL --max-time 300 -A "$UA" -w "%{http_code}" -o "$tmp" "$url")
    sz=$(wc -c < "$tmp" 2>/dev/null || echo 0)
    echo "  attempt $attempt: http=$code size=$sz"
    if [ "$code" = "200" ] && [ "$sz" -gt 204800 ]; then
      magic=$(head -c 4 "$tmp")
      if [ "$magic" = "$(printf '\x1a\x45\xdf\xa3')" ]; then
        mv "$tmp" "$dest"
        echo "  OK -> $dest ($(echo "scale=2;$sz/1024/1024"|bc) MB)"
        break
      else
        echo "  not webm"; rm -f "$tmp"; sleep 20
      fi
    else
      rm -f "$tmp"; sleep 40
    fi
  done
  sleep 15
done
echo "BATCH1 DONE"
