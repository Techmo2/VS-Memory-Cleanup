#!/usr/bin/env bash
# Builds the mod and packs it into dist/vsmemcleanup_<version>.zip
#
# Set VINTAGE_STORY to your Vintage Story install if it is not at /opt/vintagestory.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$root/src"
version="$(python3 -c "import json;print(json.load(open('$project/modinfo.json'))['version'])")"
archive="$root/dist/vsmemcleanup_$version.zip"

dotnet build "$project" -c Release -v minimal -nologo
mkdir -p "$root/dist"
rm -f "$archive"
zip -qj "$archive" "$project/bin/Release/VSMemCleanup.dll" "$project/modinfo.json"
echo "Done: $archive"
