FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY SnoutSpotter.Lambda.ExportDataset.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM public.ecr.aws/lambda/dotnet:8 AS runtime
WORKDIR /var/task
COPY --from=build /app/publish .
CMD ["SnoutSpotter.Lambda.ExportDataset::SnoutSpotter.Lambda.ExportDataset.Function::FunctionHandler"]
