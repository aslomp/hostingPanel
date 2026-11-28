FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/HostingPanel.Web/HostingPanel.Web.csproj -c Release -o /out/web /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS web
WORKDIR /app
COPY --from=build /out/web .
RUN apt-get update && apt-get install -y --no-install-recommends openssh-client && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /data && chown 1654:1654 /data
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ConnectionStrings__HostingPanel="Data Source=/data/hostingpanel.db"
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet","HostingPanel.Web.dll"]
