#!/usr/bin/env bash
# Regenerates expected-java.txt by running the same probes against the original Java
# implementation in ../../terrain-diffusion-mc. Only needed when the upstream mod changes.
#
# Requires a JDK and downloads gson into a temporary directory.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
upstream="$here/../../terrain-diffusion-mc/src/main/java/com/github/xandergos/terraindiffusionmc/pipeline"

if [ ! -d "$upstream" ]; then
  echo "Upstream Java sources not found at $upstream" >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# The harness compiles a hand-picked set of upstream files plus local stubs for the two classes
# that would otherwise drag in Fabric and the model download path.
mkdir -p "$work/src"
cp -r "$here/java-reference/." "$work/src/"
for f in FastNoiseLite PortableRng GaussianNoisePatch EDMScheduler LaplacianUtils SyntheticMapFactory; do
  cp "$upstream/$f.java" "$work/src/com/github/xandergos/terraindiffusionmc/pipeline/"
done

curl -sLo "$work/gson.jar" "https://repo1.maven.org/maven2/com/google/code/gson/gson/2.11.0/gson-2.11.0.jar"

cd "$work/src"
javac -nowarn -cp "$work/gson.jar" -d "$work/out" $(find . -name '*.java')
java -cp "$work/out:$work/gson.jar" Xcheck > "$here/expected-java.txt"

echo "Wrote $here/expected-java.txt"
