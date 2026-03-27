FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY SnoutSpotter.Lambda.IngestClip.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM public.ecr.aws/lambda/dotnet:8 AS runtime
# Install FFmpeg for keyframe extraction
RUN dnf install -y https://download1.rpmfusion.org/free/el/rpmfusion-free-release-9.noarch.rpm && \
    dnf install -y ffmpeg && \
    dnf clean all

WORKDIR /var/task
COPY --from=build /app/publish .
CMD ["SnoutSpotter.Lambda.IngestClip::SnoutSpotter.Lambda.IngestClip.Function::FunctionHandler"]
