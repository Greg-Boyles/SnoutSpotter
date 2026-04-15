FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY lambdas/SnoutSpotter.Lambda.StatsRefresh/SnoutSpotter.Lambda.StatsRefresh.csproj lambdas/SnoutSpotter.Lambda.StatsRefresh/
RUN dotnet restore lambdas/SnoutSpotter.Lambda.StatsRefresh/SnoutSpotter.Lambda.StatsRefresh.csproj
COPY lambdas/SnoutSpotter.Lambda.StatsRefresh/ lambdas/SnoutSpotter.Lambda.StatsRefresh/
RUN dotnet publish lambdas/SnoutSpotter.Lambda.StatsRefresh/SnoutSpotter.Lambda.StatsRefresh.csproj -c Release -o /app/publish

FROM public.ecr.aws/lambda/dotnet:8 AS runtime
WORKDIR /var/task
COPY --from=build /app/publish .
CMD ["SnoutSpotter.Lambda.StatsRefresh::SnoutSpotter.Lambda.StatsRefresh.Function::FunctionHandler"]
