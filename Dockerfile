FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /app

# Copy project file and restore
COPY src/GtViewer.Web/GtViewer.Web.csproj src/GtViewer.Web/
RUN dotnet restore src/GtViewer.Web/GtViewer.Web.csproj

# Copy source and publish
COPY src/GtViewer.Web/ src/GtViewer.Web/
WORKDIR /app/src/GtViewer.Web
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "GtViewer.Web.dll"]
