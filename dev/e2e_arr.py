#!/usr/bin/env python3
"""Test de bout en bout de Radarr et Sonarr (ARR=1 ./dev/e2e.sh), après e2e.py en mode démo (Alice et Bob amis).
Alice partage de vrais films et séries (vrais identifiants TMDB/TVDB : Radarr et Sonarr cherchent leurs fiches sur
Internet). Bloom les signale au Radarr et au Sonarr de Bob, puis défait ce qu'il a fait quand Alice ne les a plus.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

import e2e
from e2e import SERVERS, api, check, items, wait_idle

ARR = {
    "radarr": {"host": "http://localhost:8103", "internal": "http://radarr:7878", "config": "data/radarr/config/config.xml"},
    "sonarr": {"host": "http://localhost:8104", "internal": "http://sonarr:8989", "config": "data/sonarr/config/config.xml"},
}
MEDIA = "data/alice/media"
HD = f"{MEDIA}/movies/Deux Versions (2017)/Deux Versions (2017).mkv"  # 1280x720
SD = f"{MEDIA}/movies/Le Grand Test (2020)/Le Grand Test (2020).mkv"  # 640x360
ADDED = []  # ce que le test ajoute chez Alice, retiré à la fin


def arr(app, method, path, body=None):
    a = ARR[app]
    req = urllib.request.Request(a["host"] + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                 headers={"X-Api-Key": a["key"], "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=120) as res:
            raw = res.read()
            return res.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as err:
        raw = err.read()
        try:
            return err.code, json.loads(raw)
        except ValueError:
            return err.code, raw[:300]


def wait_arr(app):
    for _ in range(120):
        try:
            if os.path.exists(ARR[app]["config"]):
                ARR[app]["key"] = re.search(r"<ApiKey>(.*?)</ApiKey>", open(ARR[app]["config"]).read()).group(1)
                if arr(app, "GET", "/api/v3/system/status")[0] == 200:
                    return
        except (OSError, AttributeError, urllib.error.URLError, ConnectionError):
            pass
        time.sleep(2)
    raise SystemExit(app + " injoignable")


def movie_dir(title, year, tmdb, source):
    folder = f"{MEDIA}/movies/{title} ({year})"
    os.makedirs(folder, exist_ok=True)
    ADDED.append(folder)
    shutil.copy(source, f"{folder}/{title} ({year}).mkv")
    with open(f"{folder}/{title} ({year}).nfo", "w") as f:
        f.write(f'<?xml version="1.0" encoding="utf-8"?><movie><title>{title}</title><year>{year}</year>'
                f'<uniqueid type="tmdb" default="true">{tmdb}</uniqueid></movie>')
    return folder


def show_dir(title, year, tvdb, episodes):
    folder = f"{MEDIA}/shows/{title}"
    os.makedirs(f"{folder}/Season 01", exist_ok=True)
    ADDED.append(folder)
    with open(f"{folder}/tvshow.nfo", "w") as f:
        f.write(f'<?xml version="1.0" encoding="utf-8"?><tvshow><title>{title}</title><year>{year}</year>'
                f'<uniqueid type="tvdb" default="true">{tvdb}</uniqueid></tvshow>')
    for number in episodes:
        name = f"{folder}/Season 01/{title} S01E0{number}"
        shutil.copy(HD, name + ".mkv")
        with open(name + ".nfo", "w") as f:
            f.write(f'<?xml version="1.0" encoding="utf-8"?><episodedetails><title>Épisode {number}</title><season>1</season><episode>{number}</episode></episodedetails>')
    return folder


def refresh_alice_then_sync_bob(alice_id):
    api("alice", "POST", "/Library/Refresh")
    wait_idle("alice")
    api("bob", "POST", f"/Bloom/Admin/Peers/{alice_id}/Sync")
    wait_idle("bob")
    return wait_bloom()


def wait_bloom():
    """Waits for Bob's pass on Radarr and Sonarr, and returns Bob's status."""
    for _ in range(300):
        _, status = api("bob", "GET", "/Bloom/Admin/Status")
        if not status["Arr"]["Running"]:
            return status
        time.sleep(1)
    raise SystemExit("le passage de Bloom sur Radarr/Sonarr ne finit pas")


def save_arr(**overrides):
    body = {"RadarrUrl": ARR["radarr"]["internal"], "RadarrApiKey": ARR["radarr"]["key"],
            "SonarrUrl": ARR["sonarr"]["internal"], "SonarrApiKey": ARR["sonarr"]["key"],
            "Avoid": "GoodEnough", "AddFriendsItems": True}
    body.update(overrides)
    code, res = api("bob", "POST", "/Bloom/Admin/Arr", body)
    return code, res, wait_bloom() if code == 204 else None


def movies():
    return {m["tmdbId"]: m for m in arr("radarr", "GET", "/api/v3/movie")[1]}


def series():
    return {s["tvdbId"]: s for s in arr("sonarr", "GET", "/api/v3/series")[1]}


def episodes(tvdb):
    found = series().get(tvdb)
    return {} if not found else {(e["seasonNumber"], e["episodeNumber"]): e for e in arr("sonarr", "GET", f"/api/v3/episode?seriesId={found['id']}")[1]}


def tag_id(app):
    return next((t["id"] for t in arr(app, "GET", "/api/v3/tag")[1] if t["label"] == "bloom"), None)


def main():
    for key in SERVERS:
        e2e.setup_server(key)
    _, st_bob = api("bob", "GET", "/Bloom/Admin/Status")
    alice_id = st_bob["Peers"][0]["Id"]

    print("== Radarr et Sonarr de Bob")
    for app in ARR:
        wait_arr(app)
    arr("radarr", "POST", "/api/v3/rootfolder", {"path": "/movies"})
    arr("sonarr", "POST", "/api/v3/rootfolder", {"path": "/tv"})
    profiles = {p["name"]: p["id"] for p in arr("radarr", "GET", "/api/v3/qualityprofile")[1]}
    sonarr_profiles = {p["name"]: p["id"] for p in arr("sonarr", "GET", "/api/v3/qualityprofile")[1]}
    # Bob a demandé Big Buck Bunny (720p lui suffit) et Tears of Steel (en 4K) : Radarr les cherche.
    for tmdb, profile in ((10378, "HD - 720p/1080p"), (133701, "Ultra-HD")):
        code, res = arr("radarr", "POST", "/api/v3/movie", {"tmdbId": tmdb, "qualityProfileId": profiles[profile], "rootFolderPath": "/movies",
                                                         "monitored": True, "addOptions": {"searchForMovie": False}})
        check(code == 201, f"Radarr de Bob : {res.get('title') if isinstance(res, dict) else res} demandé ({profile})")
    code, res = arr("sonarr", "POST", "/api/v3/series", {"tvdbId": 81189, "title": "Breaking Bad", "qualityProfileId": sonarr_profiles["HD - 720p/1080p"],
                                                      "rootFolderPath": "/tv", "monitored": True, "seasonFolder": True,
                                                      "addOptions": {"monitor": "all", "searchForMissingEpisodes": False}})
    check(code == 201, "Sonarr de Bob : Breaking Bad suivie, tous ses épisodes surveillés")
    for _ in range(60):
        if len(episodes(81189)) > 10:
            break
        time.sleep(2)

    print("== Alice partage de vrais films et séries")
    movie_dir("Big Buck Bunny", 2008, 10378, HD)
    movie_dir("Tears of Steel", 2012, 133701, HD)
    sintel = movie_dir("Sintel", 2010, 45745, SD)
    breaking = show_dir("Breaking Bad", 2008, 81189, [1, 2])
    show_dir("The Office", 2005, 73244, [1])
    refresh_alice_then_sync_bob(alice_id)
    bob_titles = {i["Name"] for i in items("bob", "Movie")}
    check({"Big Buck Bunny", "Tears of Steel", "Sintel"} <= bob_titles, f"Bob voit les nouveaux films d'Alice ({sorted(bob_titles)})")

    print("== Bob branche Radarr et Sonarr")
    code, res, _ = save_arr(RadarrUrl="radarr:7878")
    check(code == 400 and "Radarr" in res.get("error", ""), f"adresse invalide refusée ({res})")
    code, _, status = save_arr(RadarrApiKey="mauvaise-cle")
    check(code == 204 and "clé" in (status["Arr"]["Radarr"].get("Error") or ""), f"mauvaise clé : Bob voit l'erreur ({status['Arr']['Radarr'].get('Error')})")
    code, _, status = save_arr()
    radarr_status, sonarr_status = status["Arr"]["Radarr"], status["Arr"]["Sonarr"]
    print("     Radarr :", {k: radarr_status.get(k) for k in ("Version", "Error", "NotDownloaded", "Added")})
    print("     Sonarr :", {k: sonarr_status.get(k) for k in ("Version", "Error", "NotDownloaded", "Added")})
    check(code == 204 and not radarr_status.get("Error") and not sonarr_status.get("Error") and radarr_status.get("Version"), "Bloom parle au Radarr et au Sonarr de Bob")
    check(ARR["radarr"]["key"] not in json.dumps(status) and ARR["sonarr"]["key"] not in json.dumps(status), "la page ne reçoit jamais les clés d'API")

    bloom_tag = tag_id("radarr")
    m = movies()
    check(not m[10378]["monitored"] and bloom_tag in m[10378]["tags"], "Big Buck Bunny n'est plus surveillé (Alice l'a en 720p, assez pour son profil), tag bloom")
    check(m[133701]["monitored"] and bloom_tag not in m[133701]["tags"], "Tears of Steel reste surveillé (son profil veut de la 4K, Alice l'a en 720p)")
    check(45745 in m and not m[45745]["monitored"] and bloom_tag in m[45745]["tags"], "Sintel, qu'Alice a et Bob pas, est ajouté à Radarr, non surveillé, tag bloom")
    # Les autres films d'Alice ont des identifiants TMDB de test, qui existent aussi chez TMDB : ils sont ajoutés aussi.
    added = [x["title"] for x in m.values() if bloom_tag in x["tags"] and x["tmdbId"] != 10378]
    check(radarr_status["NotDownloaded"] == 1 and radarr_status["Added"] == len(added) >= 1,
          f"la page de Bob le résume (1 film pas téléchargé, {radarr_status['Added']} ajoutés : {added})")
    eps = episodes(81189)
    show = series()
    check(not eps[(1, 1)]["monitored"] and not eps[(1, 2)]["monitored"] and eps[(1, 3)]["monitored"], "Breaking Bad : S01E01 et S01E02 (chez Alice) ne sont plus surveillés, S01E03 si")
    check(tag_id("sonarr") in show[81189]["tags"], "… et la série porte le tag bloom")
    check(73244 in show and not show[73244]["monitored"], "The Office, qu'Alice a, est ajoutée à Sonarr, non surveillée")
    check((sonarr_status["NotDownloaded"], sonarr_status["Added"]) == (2, 1), "la page de Bob le résume (2 épisodes, 1 série)")

    print("== Bob reprend la main sur un film")
    arr("radarr", "PUT", "/api/v3/movie/editor", {"movieIds": [m[10378]["id"]], "monitored": True})
    _, _, status = save_arr()
    m = movies()
    check(m[10378]["monitored"] and bloom_tag not in m[10378]["tags"], "Big Buck Bunny resurveillé par Bob : Bloom n'y touche plus et retire son tag")
    check(status["Arr"]["Radarr"]["NotDownloaded"] == 0, "… et ne le compte plus")

    print("== Alice a aussi Tears of Steel en 4K, dans une autre bibliothèque")
    folder = f"{MEDIA}/movies4k/Tears of Steel (2012)"
    os.makedirs(folder, exist_ok=True)
    ADDED.append(f"{MEDIA}/movies4k")
    subprocess.run(["docker", "exec", "bloom-alice", "/usr/lib/jellyfin-ffmpeg/ffmpeg", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc2=size=3840x2160:rate=25:duration=2", "-c:v", "libx264", "-preset", "ultrafast",
                    "-pix_fmt", "yuv420p", "/media/movies4k/Tears of Steel (2012)/Tears of Steel (2012).mkv"], check=True)
    with open(f"{folder}/Tears of Steel (2012).nfo", "w") as f:
        f.write('<?xml version="1.0" encoding="utf-8"?><movie><title>Tears of Steel</title><year>2012</year>'
                '<uniqueid type="tmdb" default="true">133701</uniqueid></movie>')
    q = urllib.parse.urlencode({"name": "Films 4K", "collectionType": "movies", "paths": "/media/movies4k", "refreshLibrary": "true"})
    api("alice", "POST", "/Library/VirtualFolders?" + q, {"LibraryOptions": {"EnableRealtimeMonitor": False}})
    wait_idle("alice")
    _, st_alice = api("alice", "GET", "/Bloom/Admin/Status")
    code, _ = api("alice", "POST", f"/Bloom/Admin/Peers/{st_alice['Peers'][0]['Id']}", {"SharedLibraryIds": [l["Id"] for l in st_alice["ShareableLibraries"]]})
    check(code == 204 and len(st_alice["ShareableLibraries"]) == 3, "Alice partage aussi sa bibliothèque « Films 4K » avec Bob")
    status = refresh_alice_then_sync_bob(alice_id)
    m = movies()
    check(not m[133701]["monitored"] and bloom_tag in m[133701]["tags"], "deux copies de Tears of Steel chez Alice (720p et 4K) : la 4K compte, Radarr arrête de le chercher")
    check(status["Arr"]["Radarr"]["NotDownloaded"] == 1, "… et la page de Bob le compte une fois")
    tears = [i for i in items("bob", "Movie") if i["Name"] == "Tears of Steel"]
    sources = []
    if len(tears) == 1:
        _, info = api("bob", "POST", f"/Items/{tears[0]['Id']}/PlaybackInfo?userId={SERVERS['bob']['user']}", {})
        sources = sorted(s["Name"] for s in info["MediaSources"])
    check(len(tears) == 1 and sources == ["Alice 4K", "Alice 720p"], f"chez Bob, une seule fiche avec les deux versions ({sources})")

    print("== Alice retire Sintel et un épisode")
    shutil.rmtree(sintel)
    for ext in (".mkv", ".nfo"):
        os.remove(f"{breaking}/Season 01/Breaking Bad S01E02{ext}")
    status = refresh_alice_then_sync_bob(alice_id)
    m = movies()
    eps = episodes(81189)
    check(45745 not in m, "Sintel, ajouté par Bloom, disparaît de Radarr")
    check(eps[(1, 2)]["monitored"] and not eps[(1, 1)]["monitored"], "S01E02 est de nouveau surveillé (Sonarr va le chercher), pas S01E01")
    commands = arr("sonarr", "GET", "/api/v3/command")[1]
    check(any(c["name"] == "EpisodeSearch" and eps[(1, 2)]["id"] in c.get("body", {}).get("episodeIds", []) for c in commands), "… et Bloom a lancé sa recherche")

    print("== Bob change d'avis")
    _, _, status = save_arr(Avoid="Off")
    eps = episodes(81189)
    check(eps[(1, 1)]["monitored"], "« Le télécharger quand même » : S01E01 est de nouveau surveillé")
    check(tag_id("sonarr") not in series()[81189]["tags"], "… et Breaking Bad perd le tag bloom")
    _, _, status = save_arr(Avoid="Off", AddFriendsItems=False)
    check(73244 not in series(), "Ne plus ajouter les séries d'amis : The Office disparaît de Sonarr")
    check(movies()[10378]["monitored"], "… sans toucher à ce que Bob a repris")
    check(status["Arr"]["Radarr"]["Added"] == 0 and status["Arr"]["Sonarr"]["Added"] == 0, "la page de Bob n'affiche plus rien d'ajouté")

    print("== Bob débranche Sonarr : Bloom défait d'abord ce qu'il y a changé")
    _, _, status = save_arr()
    check(not episodes(81189)[(1, 1)]["monitored"] and 73244 in series(), "réglages remis : S01E01 de nouveau non surveillé, The Office de nouveau ajoutée")
    _, _, status = save_arr(SonarrUrl=None, SonarrApiKey=None)
    check(episodes(81189)[(1, 1)]["monitored"] and 73244 not in series() and tag_id("sonarr") not in series()[81189]["tags"],
          "Sonarr débranché : S01E01 resurveillé, The Office retirée, plus de tag bloom")
    sonarr_status = status["Arr"]["Sonarr"]
    check(not sonarr_status.get("Url") and not sonarr_status["Releasing"] and not sonarr_status["HasApiKey"], "… puis Bloom oublie Sonarr")
    check(status["Arr"]["Radarr"].get("Url") == ARR["radarr"]["internal"], "Radarr reste branché")


if __name__ == "__main__":
    try:
        main()
    finally:
        # Les médias de test d'Alice redeviennent ceux de make-media.sh.
        for folder in ADDED:
            shutil.rmtree(folder, ignore_errors=True)
    print()
    if e2e.FAILURES:
        print(f"ÉCHEC : {len(e2e.FAILURES)} vérification(s) ratée(s)")
        for f in e2e.FAILURES:
            print("  -", f)
        sys.exit(1)
    print("Tout est OK.")
