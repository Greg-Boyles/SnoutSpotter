# Stage 1: Build .NET agent
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY shared/SnoutSpotter.Shared.Training.csproj shared/
COPY training-agent/SnoutSpotter.TrainingAgent/SnoutSpotter.TrainingAgent.csproj training-agent/SnoutSpotter.TrainingAgent/
RUN dotnet restore training-agent/SnoutSpotter.TrainingAgent/SnoutSpotter.TrainingAgent.csproj
COPY shared/ shared/
COPY training-agent/SnoutSpotter.TrainingAgent/ training-agent/SnoutSpotter.TrainingAgent/
RUN dotnet publish training-agent/SnoutSpotter.TrainingAgent/SnoutSpotter.TrainingAgent.csproj -c Release -o /app

# Stage 2: Runtime with .NET + Python + CUDA
FROM nvidia/cuda:12.1.1-runtime-ubuntu22.04

# Avoid interactive prompts during package install
ENV DEBIAN_FRONTEND=noninteractive

# .NET 8 runtime
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        dotnet-runtime-8.0 \
        python3 python3-pip \
        tar curl && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

# Python training dependencies (PyTorch + ultralytics)
RUN pip3 install --no-cache-dir \
    torch torchvision --index-url https://download.pytorch.org/whl/cu121 && \
    pip3 install --no-cache-dir ultralytics onnx

# Copy .NET agent (training scripts are downloaded from S3 at runtime)
COPY --from=build /app /app

# Create directories for runtime data
RUN mkdir -p /app/data /app/ml /app/models

WORKDIR /app
ENTRYPOINT ["dotnet", "SnoutSpotter.TrainingAgent.dll"]
