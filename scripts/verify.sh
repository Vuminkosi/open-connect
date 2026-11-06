#!/usr/bin/env sh
set -eu

run_security=false
run_contract_parity=false
for arg in "$@"; do
  case "$arg" in
    --security) run_security=true ;;
    --contract-parity) run_contract_parity=true ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done

if [ "$run_security" = true ]; then
  security_report=$(dotnet list OpenConnect.slnx package --vulnerable --include-transitive)
  printf '%s\n' "$security_report"
  if printf '%s\n' "$security_report" | grep -Eiq 'has the following vulnerable packages'; then
    echo 'Vulnerable dependencies were reported; resolve the advisories before merging.' >&2
    exit 1
  fi
fi

if [ "$run_contract_parity" = true ]; then
  contract_test_projects=$(find tests -type f -name '*Contract*.csproj' -print 2>/dev/null || true)
  if [ -n "$contract_test_projects" ]; then
    while IFS= read -r project; do
      [ -n "$project" ] || continue
      dotnet test "$project" -c Release --no-build --filter 'Category=Contract|FullyQualifiedName~Contract'
    done <<EOF
$contract_test_projects
EOF
  else
    echo 'Contract parity cannot be verified: no tests/*Contract*.csproj project was found. Add a contract test project or wire the pinned Python contract validator before allowing CI to pass.' >&2
    exit 1
  fi
fi

if [ "$run_security" = false ] && [ "$run_contract_parity" = false ]; then
  dotnet restore OpenConnect.slnx --locked-mode
  dotnet build OpenConnect.slnx -c Release --no-restore -warnaserror
  dotnet test OpenConnect.slnx -c Release --no-build
  dotnet format OpenConnect.slnx --verify-no-changes --no-restore
fi
