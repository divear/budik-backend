# Build stage using .NET 9 SDK
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project file and restore packages
COPY ["budik_backend.csproj", "./"]
RUN dotnet restore "budik_backend.csproj"

# Copy full source and publish
COPY . .
RUN dotnet publish "budik_backend.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage using .NET 9 ASP.NET runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "budik_backend.dll"]
