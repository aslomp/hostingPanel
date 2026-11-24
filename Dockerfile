FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/HostingPanel.Api/HostingPanel.Api.csproj -c Release -o /out/api /p:UseAppHost=false
RUN dotnet publish src/HostingPanel.Web/HostingPanel.Web.csproj -c Release -o /out/web /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api .
RUN mkdir -p /data && chown 1654:1654 /data
ENV ASPNETCORE_HTTP_PORTS=8080
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet","HostingPanel.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS web
WORKDIR /app
COPY --from=build /out/web .
ENV ASPNETCORE_HTTP_PORTS=8080
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet","HostingPanel.Web.dll"]
