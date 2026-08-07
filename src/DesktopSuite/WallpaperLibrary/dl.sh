#!/bin/bash
UA="Mozilla/5.0 (Windows NT 10.0; Win64; x64) WallpaperDownloader/1.0"
declare -a JOBS=(
"清晨|web-dawn-1.webm|https://upload.wikimedia.org/wikipedia/commons/f/f1/Sunrise_Timelapse_%2830174294051%29.webm"
"中午|web-midday-1.webm|https://upload.wikimedia.org/wikipedia/commons/c/c6/Cumulus_timelapse_Skupowo-170324.webm"
"黄昏|web-sunset-1.webm|https://upload.wikimedia.org/wikipedia/commons/e/e9/2020-04-01_timelapse-sunset-Belfort.webm"
"晚上|web-night-city-1.webm|https://upload.wikimedia.org/wikipedia/commons/7/79/First_nights_in_Tokyo.webm"
"深夜|web-milkyway-1.webm|https://upload.wikimedia.org/wikipedia/commons/8/80/JAPAN_Milk_Way_4K_-_Beautiful_Star_and_Sky_at_Night_Time_Lapse.webm"
)
for job in "${JOBS[@]}"; do
  IFS='|' read -r folder name url <<< "$job"
  dest="$folder/动态壁纸/$name"
  if [ -f "$dest" ]; then echo "SKIP exists: $dest"; continue; fi
  echo "=== DOWNLOAD $dest ==="
  for attempt in 1 2 3; do
    code=$(curl -sL --max-time 600 -A "$UA" -w "%{http_code}" -o "$dest" "$url")
    sz=$(wc -c < "$dest" 2>/dev/null || echo 0)
    echo "attempt $attempt: http=$code size=${sz} bytes"
    if [ "$code" = "200" ] && [ "$sz" -gt 204800 ]; then
      # verify webm magic
      magic=$(head -c 4 "$dest")
      if [ "$magic" = "$(printf '\x1a\x45\xdf\xa3')" ]; then
        echo "OK valid webm: $dest ($(echo "scale=2;$sz/1024/1024"|bc) MB)"; break
      else
        echo "NOT webm, deleting"; rm -f "$dest"
      fi
    else
      echo "retry after delay"; rm -f "$dest"; sleep 45
    fi
  done
  sleep 30
done
echo "ALL DONE"
