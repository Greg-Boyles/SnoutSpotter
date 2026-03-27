FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY SnoutSpotter.Lambda.IngestClip.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM public.ecr.aws/lambda/dotnet:8 AS runtime
# Install tar, xz and FFmpeg static build for keyframe extraction
RUN dnf install -y tar xz && \
    dnf clean all

# Download and install FFmpeg with retry logic
RUN for i in 1 2 3; do \
        curl -L --max-time 120 --retry 3 --retry-delay 5 \
        https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz \
        -o /tmp/ffmpeg.tar.xz && break || sleep 10; \
    done && \
    tar -xf /tmp/ffmpeg.tar.xz -C /tmp && \
    mv /tmp/ffmpeg-*-amd64-static/ffmpeg /usr/local/bin/ffmpeg && \
    chmod +x /usr/local/bin/ffmpeg && \
    rm -rf /tmp/ffmpeg*

WORKDIR /var/task
COPY --from=build /app/publish .
CMD ["SnoutSpotter.Lambda.IngestClip::SnoutSpotter.Lambda.IngestClip.Function::FunctionHandler"]
