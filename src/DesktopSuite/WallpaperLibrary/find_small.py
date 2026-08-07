import urllib.request, json, urllib.parse, time

UA="Mozilla/5.0 (Windows NT 10.0; Win64; x64) WallpaperDownloader/1.0"
themes={
 "dawn":"sunrise timelapse filetype:video",
 "midday":"clouds sky timelapse filetype:video",
 "sunset":"sunset timelapse filetype:video",
 "night":"city night timelapse filetype:video",
 "milkyway":"Milky Way stars timelapse filetype:video",
}
def api(search):
    q=urllib.parse.urlencode({"action":"query","generator":"search","gsrsearch":search,
        "gsrnamespace":6,"gsrlimit":20,"prop":"imageinfo","iiprop":"url|size|mime","format":"json"})
    url="https://commons.wikimedia.org/w/api.php?"+q
    req=urllib.request.Request(url, headers={"User-Agent":UA})
    return json.load(urllib.request.urlopen(req, timeout=30))

for th, s in themes.items():
    print("="*50,"\nTHEME", th)
    try:
        d=api(s)
    except Exception as e:
        print("  ERR", e); time.sleep(6); continue
    pages=d.get("query",{}).get("pages",{})
    cands=[]
    for p in pages.values():
        ii=p.get("imageinfo")
        if not ii: continue
        info=ii[0]
        if info.get("mime")=="video/webm":
            cands.append((info["size"], p["title"], info["url"]))
    cands.sort()
    for sz,t,u in cands[:8]:
        print(f"  {sz/1024/1024:.2f}MB  {t}")
    time.sleep(7)
