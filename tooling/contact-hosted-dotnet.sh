# Sourced by the existing pinned composite action's Bash steps.
dotnet() {
  /usr/bin/python3 -I -B "$GITHUB_WORKSPACE/tooling/contact-hosted-custody.py" "$(type -P dotnet)" "$@"
}
