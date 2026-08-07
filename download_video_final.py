import urllib.request, os, time

ROOT = "D:/WorkBuddy/桌面美化/src/DesktopSuite/bin/Debug/net8.0-windows/WallpaperLibrary"
UA = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
    "Accept": "video/mp4,video/*;q=0.8,*/*;q=0.5",
    "Accept-Language": "en-US,en;q=0.9",
    "Referer": "https://www.pexels.com/",
}

# period_dir, filename, pexels_video_id
JOBS = [
    ("早上", "morning-1.mp4",      "34078226"),
    ("中午", "midday-1.mp4",       "12607270"),
    ("下午", "afternoon-1.mp4",    "6960615"),
    ("傍晚", "dusk-1.mp4",         "20700707"),
    ("黄昏", "sunset-1.mp4",       "38697718"),
    ("晚上", "night-city-1.mp4",   "33637800"),
    ("深夜", "milkyway-1.mp4",     "18358235"),
]

def is_mp4(data):
    # valid mp4 starts with a size+ftyp box: bytes 4..8 == b'ftyp'
    return len(data) > 12 and data[4:8] == b"ftyp"

def download(period, fname, vid):
    dest = os.path.join(ROOT, period, "动态壁纸", fname)
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    url = f"https://www.pexels.com/download/video/{vid}/"
    for attempt in range(1, 4):
        try:
            req = urllib.request.Request(url, headers=UA)
            with urllib.request.urlopen(req, timeout=120) as r:
                data = r.read()
            if len(data) < 20000:
                print(f"  [{period}] attempt {attempt}: too small ({len(data)}B), retry")
                time.sleep(2)
                continue
            if not is_mp4(data):
                print(f"  [{period}] attempt {attempt}: not mp4 (head {data[:8].hex()}), retry")
                time.sleep(2)
                continue
            with open(dest, "wb") as f:
                f.write(data)
            print(f"  OK {period}/{fname} {len(data)} bytes")
            return True
        except Exception as e:
            print(f"  [{period}] attempt {attempt}: {type(e).__name__}: {e}")
            time.sleep(3)
    print(f"  FAIL {period}/{fname}")
    return False

if __name__ == "__main__":
    ok = 0
    for period, fname, vid in JOBS:
        print(f"== {period} (vid {vid}) ==")
        if download(period, fname, vid):
            ok += 1
    print(f"DONE video final: ok={ok} fail={len(JOBS)-ok}")
