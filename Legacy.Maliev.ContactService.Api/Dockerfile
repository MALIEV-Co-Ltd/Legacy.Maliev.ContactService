# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV GITHUB_ACTIONS=false
COPY nuget.config .
COPY .dependencies/ .dependencies/
COPY Legacy.Maliev.ContactService.Api/Legacy.Maliev.ContactService.Api.csproj Legacy.Maliev.ContactService.Api/
COPY Legacy.Maliev.ContactService.Application/Legacy.Maliev.ContactService.Application.csproj Legacy.Maliev.ContactService.Application/
COPY Legacy.Maliev.ContactService.Domain/Legacy.Maliev.ContactService.Domain.csproj Legacy.Maliev.ContactService.Domain/
COPY Legacy.Maliev.ContactService.Data/Legacy.Maliev.ContactService.Data.csproj Legacy.Maliev.ContactService.Data/
RUN dotnet restore Legacy.Maliev.ContactService.Api/Legacy.Maliev.ContactService.Api.csproj \
    -p:MalievWorkspaceRoot=/src/.dependencies
COPY . .
RUN dotnet publish Legacy.Maliev.ContactService.Api/Legacy.Maliev.ContactService.Api.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false \
    -p:MalievWorkspaceRoot=/src/.dependencies

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Legacy.Maliev.ContactService.Api.dll"]
