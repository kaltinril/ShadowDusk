#!/usr/bin/env bash
# tools/render-diagrams.sh — render docs/*.puml to docfx/images/*.svg with a PINNED,
# SHA-256-verified PlantUML. Run it before a local `dotnet docfx` build;
# .github/workflows/docs.yml runs it in CI so the published diagrams are ALWAYS
# regenerated from their .puml source (the .puml is the single source of truth).
#
# Supply-chain: PlantUML is downloaded from Maven Central (immutable artifacts) to
# tools/plantuml/ and VERIFIED against the pin below BEFORE it is ever executed — the
# same discipline tools/restore.sh applies to the native binaries. A hash mismatch is
# fatal (an unverified jar is never run). The jar is cached + gitignored.
#
# PlantUML uses the Smetana layout (set in the .puml via `!pragma layout smetana`), so
# no Graphviz is required — only a JRE.
set -euo pipefail

PLANTUML_VERSION="1.2024.8"
PLANTUML_SHA256="2e1f42a9879cd25236b5725ca7db25cb9996e8e37a0a1440b2eb559f259c54aa"
PLANTUML_URL="https://repo1.maven.org/maven2/net/sourceforge/plantuml/plantuml/${PLANTUML_VERSION}/plantuml-${PLANTUML_VERSION}.jar"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
JAR_DIR="$REPO_ROOT/tools/plantuml"
JAR="$JAR_DIR/plantuml-${PLANTUML_VERSION}.jar"
PUML_DIR="$REPO_ROOT/docs"
OUT_DIR="$REPO_ROOT/docfx/images"

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}';
    else shasum -a 256 "$1" | awk '{print $1}'; fi
}

mkdir -p "$JAR_DIR" "$OUT_DIR"

# Cache + verify the pinned jar (re-download only if absent or hash-mismatched).
if [ -f "$JAR" ] && [ "$(sha256_of "$JAR")" = "$PLANTUML_SHA256" ]; then
    echo "render-diagrams: PlantUML ${PLANTUML_VERSION} present, hash OK"
else
    echo "render-diagrams: downloading PlantUML ${PLANTUML_VERSION} from Maven Central"
    curl -fsSLo "$JAR.tmp" "$PLANTUML_URL"
    got="$(sha256_of "$JAR.tmp")"
    if [ "$got" != "$PLANTUML_SHA256" ]; then
        echo "render-diagrams: ERROR PlantUML SHA-256 mismatch (expected $PLANTUML_SHA256, got $got)" >&2
        rm -f "$JAR.tmp"; exit 1
    fi
    mv -f "$JAR.tmp" "$JAR"
    echo "render-diagrams: PlantUML ${PLANTUML_VERSION} downloaded, hash OK"
fi

if ! command -v java >/dev/null 2>&1; then
    echo "render-diagrams: ERROR java not found (PlantUML needs a JRE)" >&2; exit 1
fi

shopt -s nullglob
for puml in "$PUML_DIR"/*.puml; do
    echo "render-diagrams: $(basename "$puml") -> docfx/images/$(basename "${puml%.puml}").svg"
    java -jar "$JAR" -tsvg -o "$OUT_DIR" "$puml"
done
echo "render-diagrams: done"
