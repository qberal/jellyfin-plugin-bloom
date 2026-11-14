#!/usr/bin/env bash
# Construit le plugin, démarre deux Jellyfin (Alice et Bob) et lance le test de bout en bout.
#   ./dev/e2e.sh          → test complet sur des serveurs neufs
#   KEEP=1 ./dev/e2e.sh   → laisse tourner les serveurs après le test (http://localhost:8101 et :8102, admin/admin)
#   DEMO=1 ./dev/e2e.sh   → s'arrête quand Alice et Bob sont amis et synchronisés, et laisse tourner les serveurs
#   ARR=1 ./dev/e2e.sh    → Alice et Bob deviennent amis, puis Bloom renseigne le Radarr et le Sonarr de Bob
#                           (il faut Internet : Radarr et Sonarr y cherchent les fiches des films et séries)
set -euo pipefail
cd "$(dirname "$0")"

dotnet publish ../src/Jellyfin.Plugin.Bloom -c Release -o ../artifacts/plugin --nologo -v quiet

docker compose --profile arr down -v >/dev/null 2>&1 || true
# On vide les dossiers sans les supprimer : recréer un dossier monté peut le faire apparaître
# comme un fichier dans le conteneur (cache du partage de fichiers d'OrbStack/Docker Desktop).
for s in alice bob; do
  mkdir -p "data/$s/config" "data/$s/cache"
  find "data/$s/config" "data/$s/cache" -mindepth 1 -delete
done
for d in radarr/config radarr/movies sonarr/config sonarr/tv; do
  mkdir -p "data/$d"
  find "data/$d" -mindepth 1 -delete
done
if [ ! -f data/.media-v3 ]; then
  docker run --rm -v "$PWD":/work -w /work --entrypoint /bin/bash -e FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg jellyfin/jellyfin:12.4 ./make-media.sh
fi
for s in alice bob; do
  mkdir -p "data/$s/config/plugins/Bloom"
  cp ../artifacts/plugin/Jellyfin.Plugin.Bloom.dll "data/$s/config/plugins/Bloom/"
done

set +e
if [ "${ARR:-0}" = "1" ]; then
  docker compose --profile arr up -d
  DEMO=1 python3 -u e2e.py && python3 -u e2e_arr.py
  status=$?
  if [ "${KEEP:-0}" != "1" ]; then docker compose --profile arr down; fi
  exit $status
fi
docker compose up -d
python3 -u e2e.py
status=$?
if [ "${KEEP:-0}" != "1" ] && [ "${DEMO:-0}" != "1" ]; then docker compose down; fi
exit $status
