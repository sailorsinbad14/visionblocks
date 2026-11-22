# 用法 / Usage: source tools/env.sh
# 关闭 dotnet 遥测，并把 $VB 设成仓库根目录。如果仓库的上一级目录里有 .nuget/packages（开发时的实验环境），
# 就把 dotnet / NuGet 的缓存放在那里；没有的话用 dotnet 默认的缓存位置。
# Turns off dotnet telemetry and sets $VB to the repo root. If the parent folder has .nuget/packages
# (the original lab setup), dotnet/NuGet caches go there; otherwise dotnet's default cache is used.
VB="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export VB
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
SKILLTEST="$(dirname "$VB")"
if [ -d "$SKILLTEST/.nuget/packages" ]; then
  export SKILLTEST
  export DOTNET_CLI_HOME="$SKILLTEST/.dotnet-home"
  export NUGET_PACKAGES="$SKILLTEST/.nuget/packages"
  export NUGET_HTTP_CACHE_PATH="$SKILLTEST/.nuget/http-cache"
  export NUGET_PLUGINS_CACHE_PATH="$SKILLTEST/.nuget/plugins-cache"
fi
export PATH="/c/Program Files/dotnet:$PATH"
