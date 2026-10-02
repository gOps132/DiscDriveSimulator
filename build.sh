#!/bin/bash
set -e

echo "=== Installing .NET 10 SDK ==="
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --install-dir "$HOME/.dotnet"

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

echo "=== .NET SDK Version ==="
dotnet --version

echo "=== Building Tailwind CSS ==="
if command -v npm >/dev/null 2>&1; then
  npm ci --no-audit --no-fund
  npm run css:build
else
  echo "npm not found, using checked-in wwwroot/css/tailwind.css"
fi

echo "=== Publishing Blazor WebAssembly ==="
dotnet publish DiscDriveSimulator.csproj -c Release -o dist
