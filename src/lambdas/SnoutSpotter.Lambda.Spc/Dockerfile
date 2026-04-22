FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Build context is src/ — COPY paths are relative to that so we can pull in the
# shared project referenced from the Lambda csproj.
COPY shared/SnoutSpotter.Spc.Client/SnoutSpotter.Spc.Client.csproj shared/SnoutSpotter.Spc.Client/
COPY lambdas/SnoutSpotter.Lambda.Spc/SnoutSpotter.Lambda.Spc.csproj lambdas/SnoutSpotter.Lambda.Spc/
RUN dotnet restore lambdas/SnoutSpotter.Lambda.Spc/SnoutSpotter.Lambda.Spc.csproj
COPY shared/SnoutSpotter.Spc.Client/ shared/SnoutSpotter.Spc.Client/
COPY lambdas/SnoutSpotter.Lambda.Spc/ lambdas/SnoutSpotter.Lambda.Spc/
RUN dotnet publish lambdas/SnoutSpotter.Lambda.Spc/SnoutSpotter.Lambda.Spc.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# Lambda Web Adapter — proxies Lambda events to the ASP.NET app on port 8080
COPY --from=public.ecr.aws/awsguru/aws-lambda-adapter:0.8.4 /lambda-adapter /opt/extensions/lambda-adapter
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV AWS_LAMBDA_EXEC_WRAPPER=/opt/bootstrap
ENTRYPOINT ["dotnet", "SnoutSpotter.Lambda.Spc.dll"]
