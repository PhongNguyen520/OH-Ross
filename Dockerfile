FROM mcr.microsoft.com/playwright/dotnet:v1.58.0-noble AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["OH-Ross/OH-Ross.csproj", "OH-Ross/"]
RUN dotnet restore "OH-Ross/OH-Ross.csproj"
COPY . .
WORKDIR "/src/OH-Ross"
RUN dotnet build "OH-Ross.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "OH-Ross.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OH-Ross.dll"]
