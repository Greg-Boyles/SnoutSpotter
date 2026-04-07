#!/bin/bash
set -euo pipefail

# updater.sh — runs on host, manages training agent container lifecycle.
# Watches the container exit code:
#   0  = clean shutdown
#   42 = update requested (pull new image and restart)
#   *  = crash (restart after cooldown)

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Source ECR registry from .env if present
if [ -f .env ]; then
    set -a; source .env; set +a
fi

ecr_login() {
    local REGISTRY="${ECR_REGISTRY:-}"
    if [ -n "$REGISTRY" ]; then
        echo "Authenticating to ECR..."
        aws ecr get-login-password --region "${AWS_REGION:-eu-west-1}" | \
            docker login --username AWS --password-stdin "$REGISTRY"
    fi
}

echo "=== SnoutSpotter Training Agent Updater ==="
echo "Press Ctrl+C to stop"

while true; do
    echo "Starting training agent..."
    docker compose up trainer
    EXIT_CODE=$?

    if [ "$EXIT_CODE" -eq 42 ]; then
        echo "Agent requested update — pulling new image..."
        ecr_login
        docker compose pull trainer
        echo "Restarting with new image..."
    elif [ "$EXIT_CODE" -eq 0 ]; then
        echo "Agent exited cleanly — stopping"
        break
    else
        echo "Agent crashed (code=$EXIT_CODE) — restarting in 10s..."
        sleep 10
    fi
done
