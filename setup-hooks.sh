#!/bin/bash

# Script to set up git hooks for the repository
# This configures git to use the hooks from the .githooks directory

echo "Setting up git hooks..."

# Configure git to use the .githooks directory
git config core.hooksPath .githooks

if [ $? -eq 0 ]; then
    echo "✅ Git hooks configured successfully!"
    echo ""
    echo "The pre-commit hook will now automatically check code formatting before each commit."
    echo ""
    echo "To format your code, run: dotnet format"
else
    echo "❌ Failed to configure git hooks"
    exit 1
fi