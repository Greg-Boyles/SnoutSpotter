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

# Download and install FFmpeg with retry logic and verification
RUN set -e && \
    SUCCESS=0 && \
    for i in 1 2 3 4 5; do \
        echo "Attempt $i: Downloading FFmpeg..." && \
        curl -f -L --connect-timeout 30 --max-time 180 --retry 5 --retry-delay 10 \
        https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz \
        -o /tmp/ffmpeg.tar.xz && SUCCESS=1 && break || \
        (echo "Download failed, waiting before retry..." && sleep 15); \
    done && \
    if [ $SUCCESS -eq 0 ]; then echo "Failed to download FFmpeg after all retries" && exit 1; fi && \
    echo "Extracting FFmpeg..." && \
    tar -xf /tmp/ffmpeg.tar.xz -C /tmp && \
    mv /tmp/ffmpeg-*-amd64-static/ffmpeg /usr/local/bin/ffmpeg && \
    chmod +x /usr/local/bin/ffmpeg && \
    /usr/local/bin/ffmpeg -version && \
    rm -rf /tmp/ffmpeg*

WORKDIR /var/task
COPY --from=build /app/publish .
CMD ["SnoutSpotter.Lambda.IngestClip::SnoutSpotter.Lambda.IngestClip.Function::FunctionHandler"]
