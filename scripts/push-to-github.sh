#!/usr/bin/env bash
set -euo pipefail
GITHUB_USER="vruppuluri"
REPO_NAME="fhir-bulk-loader"
REPO_DESC="FHIR Bulk Loader & Export — Azure Function App with Terraform"
BRANCH="main"

echo "▶  Initialising git repo …"
git init -b "$BRANCH"
git add -A
git commit -m "feat: initial FHIR Bulk Loader & Export"

echo "▶  Creating GitHub repo ${GITHUB_USER}/${REPO_NAME} …"
if command -v gh &>/dev/null; then
  gh repo create "${GITHUB_USER}/${REPO_NAME}" \
    --description "$REPO_DESC" --public --source=. --remote=origin --push
else
  : "${GITHUB_TOKEN:?Set GITHUB_TOKEN or install gh CLI}"
  curl -s -X POST -H "Authorization: token $GITHUB_TOKEN" \
       -H "Content-Type: application/json" \
       "https://api.github.com/user/repos" \
       -d "{\"name\":\"${REPO_NAME}\",\"description\":\"${REPO_DESC}\",\"private\":false}"
  git remote add origin "https://${GITHUB_TOKEN}@github.com/${GITHUB_USER}/${REPO_NAME}.git"
  git push -u origin "$BRANCH"
fi

echo ""
echo "✅  https://github.com/${GITHUB_USER}/${REPO_NAME}"
echo "Next: add GitHub Secrets then push to main to deploy."
