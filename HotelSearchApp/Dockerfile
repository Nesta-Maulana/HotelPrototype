# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj files and restore dependencies
COPY ["HotelSearchApp.Web/HotelSearchApp.Web.csproj", "HotelSearchApp.Web/"]
COPY ["HotelSearchApp.Core/HotelSearchApp.Core.csproj", "HotelSearchApp.Core/"]
COPY ["HotelSearchApp.Infrastructure/HotelSearchApp.Infrastructure.csproj", "HotelSearchApp.Infrastructure/"]
RUN dotnet restore "HotelSearchApp.Web/HotelSearchApp.Web.csproj"

# Copy everything else and build
COPY . .
WORKDIR "/src/HotelSearchApp.Web"
RUN dotnet build "HotelSearchApp.Web.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "HotelSearchApp.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Copy published app
COPY --from=publish /app/publish .

# Set environment variable for Elasticsearch URL (can be overridden)
ENV ASPNETCORE_URLS=http://+:80
ENV Elasticsearch__Url=http://elasticsearch:9200
ENV Elasticsearch__Index=hotels

ENTRYPOINT ["dotnet", "HotelSearchApp.Web.dll"]