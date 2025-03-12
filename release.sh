#!/usr/bin/env bash
cd "$(dirname "$(realpath $0)")"
shopt -s extglob globstar

dotnet build
path=$(mktemp -u --suffix=.zip)
zip $path -j icon.png manifest.json README.md bin/**/!(System.*|Newtonsoft.Json).dll
echo "Zip created at $path"
