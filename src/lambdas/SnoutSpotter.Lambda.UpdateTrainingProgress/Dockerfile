FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY shared/SnoutSpotter.Shared.Training.csproj shared/
COPY lambdas/SnoutSpotter.Lambda.UpdateTrainingProgress/SnoutSpotter.Lambda.UpdateTrainingProgress.csproj lambdas/SnoutSpotter.Lambda.UpdateTrainingProgress/
RUN dotnet restore lambdas/SnoutSpotter.Lambda.UpdateTrainingProgress/SnoutSpotter.Lambda.UpdateTrainingProgress.csproj
COPY shared/ shared/
COPY lambdas/SnoutSpotter.Lambda.UpdateTrainingProgress/ lambdas/SnoutSpotter.Lambda.UpdateTrainingProgress/
RUN dotnet publish lambdas/SnoutSpotter.Lambda.UpdateTrainingProgress/SnoutSpotter.Lambda.UpdateTrainingProgress.csproj -c Release -o /app/publish

FROM public.ecr.aws/lambda/dotnet:8 AS runtime
WORKDIR /var/task
COPY --from=build /app/publish .
CMD ["SnoutSpotter.Lambda.UpdateTrainingProgress::SnoutSpotter.Lambda.UpdateTrainingProgress.Function::FunctionHandler"]
