#!/usr/bin/env bash
# Publishes the canonical docs in documentation/backend/ to the GitHub Wiki.
# The Wiki mirrors the repo files; the repo files stay authoritative.
#
# Usage: scripts/sync-wiki-docs.sh
# Requires: git, gh (authenticated with repo push access).
# First-time setup: open the Wiki tab on GitHub once so the wiki
# repository is initialized, then run this script.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$REPO_ROOT/documentation/backend"
META="$REPO_ROOT/documentation/wiki-meta"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

git clone --quiet https://github.com/mtsarac/mail-client.wiki.git "$WORK/wiki"
cd "$WORK/wiki"

copy_page() {
  local src="$1" dest="$2"
  sed -e 's](BACKEND_GUIDE\.en\.md)](Backend-Guide)]g' \
      -e 's](ARCHITECTURE\.en\.md)](Architecture)]g' \
      -e 's](API_REFERENCE\.en\.md)](API-Reference)]g' \
      -e 's](DEVELOPMENT\.en\.md)](Development)]g' \
      -e 's](SECURITY\.en\.md)](Security)]g' \
      -e 's](BACKEND_GUIDE\.tr\.md)](Backend-Kilavuzu)]g' \
      -e 's](ARCHITECTURE\.tr\.md)](Mimari)]g' \
      -e 's](API_REFERENCE\.tr\.md)](API-Referansi)]g' \
      -e 's](DEVELOPMENT\.tr\.md)](Gelistirme)]g' \
      -e 's](SECURITY\.tr\.md)](Guvenlik)]g' \
      "$SRC/$src" > "$dest"
}

copy_page BACKEND_GUIDE.en.md Backend-Guide.md
copy_page ARCHITECTURE.en.md Architecture.md
copy_page API_REFERENCE.en.md API-Reference.md
copy_page DEVELOPMENT.en.md Development.md
copy_page SECURITY.en.md Security.md
copy_page BACKEND_GUIDE.tr.md Backend-Kilavuzu.md
copy_page ARCHITECTURE.tr.md Mimari.md
copy_page API_REFERENCE.tr.md API-Referansi.md
copy_page DEVELOPMENT.tr.md Gelistirme.md
copy_page SECURITY.tr.md Guvenlik.md
cp "$META/Home.md" Home.md
cp "$META/_Sidebar.md" _Sidebar.md
cp "$META/_Footer.md" _Footer.md

git add -A
if git diff --cached --quiet; then
  echo "Wiki already in sync."
  exit 0
fi
git -c user.name="mtsarac" -c user.email="mtsarac@users.noreply.github.com" \
  commit -m "docs: sync wiki from documentation/backend"
git push origin master 2>/dev/null || git push origin HEAD:master
echo "Wiki published."
