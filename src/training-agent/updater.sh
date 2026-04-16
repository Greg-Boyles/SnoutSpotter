#!/bin/bash
set -euo pipefail

# updater.sh — runs on host, manages training agent container lifecycle.
# Watches the container exit code:
#   0  = clean shutdown
#   42 = update requested (pull new image and restart)
#   *  = crash (restart after cooldown)
#
# Create a .env file in this directory before running:
#
#   ECR_REGISTRY=123456789012.dkr.ecr.eu-west-1.amazonaws.com   # required: ECR registry
#   AGENT_NAME=gregs-pc                                          # required on first run only
#   TRAINER_REGISTRATION_URL=https://...                         # optional: defaults to PiMgmt API
#
# On first run the agent self-registers, saves certs and config to the trainer-state
# Docker volume, and starts. On subsequent runs AGENT_NAME is not needed.

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
        if [ -f host-state/pending-version ]; then
            export IMAGE_TAG="v$(cat host-state/pending-version)"
            echo "Agent requested update to $IMAGE_TAG"
            rm -f host-state/pending-version
            sed -i '/^IMAGE_TAG=/d' .env 2>/dev/null
            echo "IMAGE_TAG=$IMAGE_TAG" >> .env
        fi
        echo "Pulling new image..."
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
