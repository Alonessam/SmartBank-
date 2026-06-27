# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /app

# Copy solution file and restore dependencies
COPY SmartBank.slnx ./
COPY src/SmartBank.Core/SmartBank.Core.csproj ./src/SmartBank.Core/
COPY src/SmartBank.Infrastructure/SmartBank.Infrastructure.csproj ./src/SmartBank.Infrastructure/
COPY src/SmartBank.API/SmartBank.API.csproj ./src/SmartBank.API/
COPY src/SmartBank.Tests/SmartBank.Tests.csproj ./src/SmartBank.Tests/
RUN dotnet restore src/SmartBank.API/SmartBank.API.csproj

# Copy remaining files and build
COPY . ./
RUN dotnet publish src/SmartBank.API/SmartBank.API.csproj -c Release -o out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build-env /app/out .

# Expose ports
EXPOSE 80
EXPOSE 443

# Start application
ENTRYPOINT ["dotnet", "SmartBank.API.dll"]
