# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.
# Build context is the repository root, not this folder.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# Builds the Vue SPA; its dist/ is served as static files by the API.
FROM node:22-alpine AS client
WORKDIR /src/clientapp
COPY ["clientapp/package.json", "clientapp/package-lock.json", "./"]
RUN npm ci
COPY clientapp/ ./
RUN npm run build

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["forms/forms.csproj", "forms/"]
RUN dotnet restore "./forms/forms.csproj"
COPY . .
WORKDIR "/src/forms"
RUN dotnet build "./forms.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./forms.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=client /src/clientapp/dist ./wwwroot
ENTRYPOINT ["dotnet", "forms.dll"]
