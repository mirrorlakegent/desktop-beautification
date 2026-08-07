import urllib.request, re, os, sys

ROOT = "D:/WorkBuddy/桌面美化/src/DesktopSuite/bin/Debug/net8.0-windows/WallpaperLibrary"
UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"}

# term -> (period_dir, filename)
JOBS = [
    ("morning",      "早上", "morning-1.mp4"),
    ("midday",       "中午", "midday-1.mp4"),
    ("afternoon",    "下午", "afternoon-1.mp4"),
    ("dusk",         "傍晚", "dusk-1.mp4"),
    ("sunset",       "黄昏", "sunset-1.mp4"),
    ("city+night",   "晚上", "night-city-1.mp4"),
    ("milky+way",    "深夜", "milkyway-1.mp4"),
]

def fetch(url):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.read().decode("utf-8", "ignore")

def get_ids(term):
    html = fetch(f"https://www.pexels.com/search/videos/{term}/")
    # download links look like /download/video/<id>/
    ids = re.findall(r'/download/video/(\d+)/', html)
    # also video page links /video/<slug>-<id>/
    if not ids:
        ids = re.findall(r'/video/[^"\']+?-(\d+)/', html)
    seen = []
    for i in ids:
        if i not in seen:
            seen.append(i)
    return seen

def download(term, period, fname):
    ids = get_ids(term)
    print(f"[{period}] term={term} candidates={ids[:5]}")
    if not ids:
        print(f"  !! no ids found for {term}")
        return False
    dest = os.path.join(ROOT, period, "动态壁纸", fname)
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    vid = ids[0]
    # follow redirect from download endpoint to real mp4
    url = f"https://www.pexels.com/download/video/{vid}/"
    req = urllib.request.Request(url, headers=UA)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            data = r.read()
    except urllib.error.HTTPError as e:
        # sometimes needs the redirect chain; grab Location
        print(f"  HTTPError {e.code}: {e.headers.get('Location')}")
        return False
    if len(data) < 5000:
        print(f"  !! too small ({len(data)} bytes) for {term}")
        return False
    with open(dest, "wb") as f:
        f.write(data)
    print(f"  OK {dest} {len(data)} bytes")
    return True

if __name__ == "__main__":
    for term, period, fname in JOBS:
        try:
            download(term, period, fname)
        except Exception as e:
            print(f"  EXCEPTION {period}: {e}")
    print("DONE video v2")
