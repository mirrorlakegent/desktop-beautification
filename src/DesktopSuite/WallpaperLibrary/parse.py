import json, os

themes = {
    "dawn": "dawn.json",
    "midday": "midday.json",
    "sunset": "sunset.json",
    "night": "night.json",
    "milkyway": "milkyway.json",
}

for theme, fname in themes.items():
    print("="*60)
    print("THEME:", theme)
    try:
        data = json.load(open(fname, encoding="utf-8"))
    except Exception as e:
        print("  parse error", e); continue
    pages = data.get("query", {}).get("pages", {})
    cands = []
    for pid, p in pages.items():
        title = p.get("title","")
        ii = p.get("imageinfo")
        if not ii: continue
        info = ii[0]
        url = info.get("url","")
        mime = info.get("mime","")
        size = info.get("size", 0)
        cands.append((title, mime, size, url))
    # prefer webm, then ogg; larger first
    def rank(c):
        title, mime, size, url = c
        r = 0
        if mime == "video/webm": r += 100
        elif mime in ("video/ogg","application/ogg"): r += 50
        r += min(size, 50_000_000)/1_000_000.0
        return r
    cands.sort(key=rank, reverse=True)
    for title, mime, size, url in cands[:6]:
        print(f"  [{mime}] {size/1024/1024:.2f}MB  {title}")
        print(f"     {url}")
