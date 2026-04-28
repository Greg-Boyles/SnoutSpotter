FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY shared/SnoutSpotter.Spc.Client/SnoutSpotter.Spc.Client.csproj shared/SnoutSpotter.Spc.Client/
COPY lambdas/SnoutSpotter.Lambda.SpcPoller/SnoutSpotter.Lambda.SpcPoller.csproj lambdas/SnoutSpotter.Lambda.SpcPoller/
RUN dotnet restore lambdas/SnoutSpotter.Lambda.SpcPoller/SnoutSpotter.Lambda.SpcPoller.csproj
COPY shared/SnoutSpotter.Spc.Client/ shared/SnoutSpotter.Spc.Client/
COPY lambdas/SnoutSpotter.Lambda.SpcPoller/ lambdas/SnoutSpotter.Lambda.SpcPoller/
RUN dotnet publish lambdas/SnoutSpotter.Lambda.SpcPoller/SnoutSpotter.Lambda.SpcPoller.csproj -c Release -o /app/publish

FROM public.ecr.aws/lambda/dotnet:8 AS runtime
WORKDIR /var/task
COPY --from=build /app/publish .
CMD ["SnoutSpotter.Lambda.SpcPoller::SnoutSpotter.Lambda.SpcPoller.Function::Handle"]
