FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY shared/SnoutSpotter.Contracts/SnoutSpotter.Contracts.csproj shared/SnoutSpotter.Contracts/
COPY shared/SnoutSpotter.Shared.Training.csproj shared/
COPY api/SnoutSpotter.Api.csproj api/
RUN dotnet restore api/SnoutSpotter.Api.csproj
COPY shared/ shared/
COPY api/ api/
RUN dotnet publish api/SnoutSpotter.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# Lambda Web Adapter — proxies Lambda events to the ASP.NET app on port 8080
COPY --from=public.ecr.aws/awsguru/aws-lambda-adapter:0.8.4 /lambda-adapter /opt/extensions/lambda-adapter
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV AWS_LAMBDA_EXEC_WRAPPER=/opt/bootstrap
ENTRYPOINT ["dotnet", "SnoutSpotter.Api.dll"]
