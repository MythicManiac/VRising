#!/usr/bin/env bash

if [[ $1 ]]; then
  dotnet build . --configuration Release --output ./ReleaseOutput -p:Version=$1
  find ./ReleaseOutput ! -name ServerLaunchFix.dll ! -name ServerLaunchFix.xml -type f -delete
  tcli publish --package-version $1
else
  echo "Version number missing"
fi
