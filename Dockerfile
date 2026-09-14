FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Server.csproj ./
RUN dotnet restore Server.csproj
COPY . ./
RUN dotnet publish Server.csproj -c Release -o /app/publish --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
ENV PORT=10000
COPY --from=build /app/publish ./
EXPOSE 10000
ENTRYPOINT ["dotnet", "Server.dll"]