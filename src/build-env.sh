# Source this before any dotnet command in fa.storytime.
#   source src/build-env.sh
# System dotnet at /usr/local/share/dotnet cannot build this repo (no workload write perms).
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export JAVA_HOME="/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home"
export ANDROID_HOME="$HOME/Library/Android/sdk"
# Model binaries live OUTSIDE the iCloud-synced tree (eviction/xattr safety).
export STORYTIME_MODEL_ROOT="${STORYTIME_MODEL_ROOT:-$HOME/StoryTimeModels}"
