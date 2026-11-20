# Source this before any dotnet command in labs.fa.story-time (clone: ~/src/labs.fa.story-time).
#   source src/build-env.sh
# System dotnet at /usr/local/share/dotnet cannot build this repo (no workload write perms);
# the user-local SDK under ~/.dotnet carries the maui/ios workloads.
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
