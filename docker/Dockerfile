# syntax=docker/dockerfile:1
#
# UM Dockerfile para os 5 aplicativos. O estágio "build" compila tudo uma única vez; cada alvo final
# (identity, tickets, tenants, notifications, gateway) copia só o resultado do seu aplicativo.
#
#   docker build -f docker/Dockerfile --target identity -t helpdeskflow/identity .
#
# O contexto de build é a RAIZ do repositório.

# ───────────── Build ─────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /repo

# Restaurar pacotes antes de copiar o código aproveita o cache do Docker quando só o código muda.
COPY src/ src/
RUN dotnet restore src/Services/Identity/Identity.Api \
 && dotnet restore src/Services/Tickets/Tickets.Api \
 && dotnet restore src/Services/Tenants/Tenants.Api \
 && dotnet restore src/Services/Notifications/Notifications.Api \
 && dotnet restore src/Gateway/Gateway.Api

RUN dotnet publish src/Services/Identity/Identity.Api         -c Release -o /out/identity      --no-restore /p:UseAppHost=false \
 && dotnet publish src/Services/Tickets/Tickets.Api           -c Release -o /out/tickets       --no-restore /p:UseAppHost=false \
 && dotnet publish src/Services/Tenants/Tenants.Api           -c Release -o /out/tenants       --no-restore /p:UseAppHost=false \
 && dotnet publish src/Services/Notifications/Notifications.Api -c Release -o /out/notifications --no-restore /p:UseAppHost=false \
 && dotnet publish src/Gateway/Gateway.Api                    -c Release -o /out/gateway       --no-restore /p:UseAppHost=false

# ───────────── Base de execução ─────────────
# Só o runtime do ASP.NET (sem SDK): imagem menor e com menos superfície de ataque.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0
# Nunca roda como root: a imagem oficial já traz o usuário "app" (UID em $APP_UID).
USER $APP_UID
EXPOSE 8080

# ───────────── Alvos finais ─────────────
FROM runtime AS identity
COPY --from=build /out/identity .
ENTRYPOINT ["dotnet", "Identity.Api.dll"]

FROM runtime AS tickets
COPY --from=build /out/tickets .
ENTRYPOINT ["dotnet", "Tickets.Api.dll"]

FROM runtime AS tenants
COPY --from=build /out/tenants .
ENTRYPOINT ["dotnet", "Tenants.Api.dll"]

FROM runtime AS notifications
COPY --from=build /out/notifications .
ENTRYPOINT ["dotnet", "Notifications.Api.dll"]

FROM runtime AS gateway
COPY --from=build /out/gateway .
ENTRYPOINT ["dotnet", "Gateway.Api.dll"]
