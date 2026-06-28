#!/usr/bin/env bash
# Демо за dev tunnels: создаёт постоянные туннели на Kestrel-порты сервисов, прописывает публичные
# URL в user-secrets AppHost (DemoTunnels:*), публикует туннели. Подробности и гочи — docs/DEMO_TUNNELS.md.
#
#   scripts/demo-tunnels.sh up      # создать туннели + прописать user-secrets (один раз/после смены)
#   dotnet run --project ChessSchool.AppHost   # поднять стенд (в отдельном терминале)
#   scripts/demo-tunnels.sh host    # опубликовать туннели (держать запущенным, пока идёт демо)
#   scripts/demo-tunnels.sh clear   # выключить демо-режим: убрать user-secrets и удалить туннель
#
# Порты — Kestrel https из launchSettings (фиксированы), их и форвардим напрямую (минуя прокси Aspire).
# Скрипт опирается на соглашения CLI `devtunnel`; если флаги в твоей версии отличаются — см. рунбук.
set -euo pipefail

TUNNEL_ID="${TUNNEL_ID:-chess-demo}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APPHOST_DIR="$ROOT/ChessSchool.AppHost"

AUTH_PORT=7139    # ChessSchool.Auth
WEB_PORT=7108     # ChessSchool.Web
ARENA_PORT=7167   # ChessSchool.Arena
GAME_PORT=7123    # ChessSchool.GameServer
PORTS=("$AUTH_PORT" "$WEB_PORT" "$ARENA_PORT" "$GAME_PORT")

command -v devtunnel >/dev/null 2>&1 || { echo "Нужен devtunnel CLI: https://aka.ms/devtunnel/install"; exit 1; }

cmd="${1:-up}"
case "$cmd" in
  up)
    devtunnel user show >/dev/null 2>&1 || { echo "Сначала войди: devtunnel user login"; exit 1; }

    # Идемпотентно: туннель + порты + анонимный доступ (чтобы тестировщикам не логиниться в сам туннель).
    devtunnel show "$TUNNEL_ID" >/dev/null 2>&1 || devtunnel create "$TUNNEL_ID" >/dev/null
    for p in "${PORTS[@]}"; do
      devtunnel port create "$TUNNEL_ID" -p "$p" >/dev/null 2>&1 || true
    done
    devtunnel access create "$TUNNEL_ID" --anonymous >/dev/null 2>&1 || true

    show="$(devtunnel show "$TUNNEL_ID")"
    url_for() { echo "$show" | grep -oE "https://[a-z0-9-]+-$1\.[a-z0-9.-]*devtunnels\.ms" | head -1; }
    AUTH_URL="$(url_for "$AUTH_PORT")"; WEB_URL="$(url_for "$WEB_PORT")"
    ARENA_URL="$(url_for "$ARENA_PORT")"; GAME_URL="$(url_for "$GAME_PORT")"

    if [ -z "$AUTH_URL" ] || [ -z "$WEB_URL" ] || [ -z "$ARENA_URL" ] || [ -z "$GAME_URL" ]; then
      echo "Не удалось извлечь публичные URL из 'devtunnel show'. Вывод ниже — пропиши вручную (см. рунбук):"
      echo "$show"
      exit 1
    fi

    ( cd "$APPHOST_DIR"
      dotnet user-secrets set "DemoTunnels:Auth"       "$AUTH_URL"  >/dev/null
      dotnet user-secrets set "DemoTunnels:Web"        "$WEB_URL"   >/dev/null
      dotnet user-secrets set "DemoTunnels:Arena"      "$ARENA_URL" >/dev/null
      dotnet user-secrets set "DemoTunnels:GameServer" "$GAME_URL"  >/dev/null )

    echo "Демо-режим включён. Публичные адреса для тестировщиков:"
    echo "  Школа/лендинг: $WEB_URL"
    echo "  Арена:         $ARENA_URL"
    echo "  Онлайн-партия: $WEB_URL/play"
    echo
    echo "Дальше:"
    echo "  1) dotnet run --project ChessSchool.AppHost"
    echo "  2) $0 host   # публикует туннели (держи запущенным)"
    ;;

  host)
    echo "Публикую туннели $TUNNEL_ID (Ctrl+C — остановить)…"
    devtunnel host "$TUNNEL_ID"
    ;;

  clear|down)
    ( cd "$APPHOST_DIR"
      for k in Auth Web Arena GameServer; do
        dotnet user-secrets remove "DemoTunnels:$k" >/dev/null 2>&1 || true
      done )
    devtunnel delete "$TUNNEL_ID" >/dev/null 2>&1 || true
    echo "Демо-режим выключен: user-secrets очищены, туннель $TUNNEL_ID удалён."
    ;;

  *)
    echo "Использование: $0 [up|host|clear]"
    exit 1
    ;;
esac
