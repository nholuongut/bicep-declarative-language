#!/bin/bash

# Ignore any PRs before this date
AFTER="2021-06-15"

# The repos to approve & merge PRs for
REPOS=(
  "nholuongut/bicep-declarative-language"
  "nholuongut/bicep-declarative-language-types-az"
  "nholuongut/bicep-declarative-language-types-k8s"
  "nholuongut/bicep-declarative-language-shared-tools"
)

for REPO in "${REPOS[@]}"; do
  PRS=$(gh pr list -R $REPO --search "is:open is:pr author:app/dependabot review:required created:>$AFTER" | awk '{print $1}')
  while read -r PR && [ "$PR" != "" ]; do
    gh pr merge -R $REPO --auto --squash $PR
    gh pr review -R $REPO --approve $PR
  done <<< "$PRS"
done