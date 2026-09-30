#!/bin/bash
set -e

echo "=== Installing .NET 10 SDK ==="
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --install-dir "$HOME/.dotnet"

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

echo "=== .NET SDK Version ==="
dotnet --version

echo "=== Publishing Blazor WebAssembly ==="
dotnet publish DiscDriveSimulator.csproj -c Release -o dist
