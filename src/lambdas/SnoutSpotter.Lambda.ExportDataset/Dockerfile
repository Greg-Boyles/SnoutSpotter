FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY shared/SnoutSpotter.Contracts/SnoutSpotter.Contracts.csproj shared/SnoutSpotter.Contracts/
COPY lambdas/SnoutSpotter.Lambda.ExportDataset/SnoutSpotter.Lambda.ExportDataset.csproj lambdas/SnoutSpotter.Lambda.ExportDataset/
RUN dotnet restore lambdas/SnoutSpotter.Lambda.ExportDataset/SnoutSpotter.Lambda.ExportDataset.csproj
COPY shared/ shared/
COPY lambdas/SnoutSpotter.Lambda.ExportDataset/ lambdas/SnoutSpotter.Lambda.ExportDataset/
RUN dotnet publish lambdas/SnoutSpotter.Lambda.ExportDataset/SnoutSpotter.Lambda.ExportDataset.csproj -c Release -o /app/publish

FROM public.ecr.aws/lambda/dotnet:8 AS runtime
WORKDIR /var/task
COPY --from=build /app/publish .
CMD ["SnoutSpotter.Lambda.ExportDataset::SnoutSpotter.Lambda.ExportDataset.Function::FunctionHandler"]
