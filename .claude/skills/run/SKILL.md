---
name: run
description: Build the Zeus frontend into wwwroot, start the Vite dev server, and start the Zeus.Server backend. Kills any process already bound to the target ports first. Optional portOffset argument (e.g. `/run 10`) shifts both ports by that amount.
---

# /run — start Zeus full stack

Bring up Zeus for local development:

1. Free the target ports (kill any existing listeners).
2. Build the frontend into `Zeus.Server/wwwroot`.
3. Start the Vite dev server (live-reload frontend).
4. Start the .NET backend.
5. Report the bound ports back to the user.

## Argument

- `$1` (optional): integer **portOffset**, default `0`. Shifts both ports.
  - `/run` → Vite **5173**, backend **6060**
  - `/run 10` → Vite **5183**, backend **6070**
  - `/run 100` → Vite **5273**, backend **6160**
- Reject negative values — tell the user, don't proceed.

## Port configuration (how offset works)

Both services already read their ports from env vars — no code patching needed:

- Backend (`Zeus.Server/Program.cs`): reads `ZEUS_PORT` env var, defaults to 6060. Still uses `ListenAnyIP` so LAN access is preserved.
- Frontend (`zeus-web/vite.config.ts`): `/api` and `/ws` proxy target reads `BACKEND_PORT` env var, defaults to 6060. Vite's own listen port is set via `--port` on the CLI.

## Steps

### 1. Parse offset

```bash
OFFSET="${1:-0}"
# reject non-integer / negative
case "$OFFSET" in
  ''|*[!0-9]*) echo "portOffset must be a non-negative integer"; exit 1 ;;
esac
FRONTEND_PORT=$((5173 + OFFSET))
BACKEND_PORT=$((6060 + OFFSET))
```

### 2. Kill existing listeners on both target ports

```bash
lsof -ti :"$FRONTEND_PORT" | xargs kill -9 2>/dev/null; \
lsof -ti :"$BACKEND_PORT"  | xargs kill -9 2>/dev/null; \
sleep 1
```

Do NOT use `fuser` (not default on macOS).

### 3. Build the frontend into wwwroot

```bash
npm --prefix zeus-web run build
```

This runs `tsc -b && vite build` and writes to `Zeus.Server/wwwroot/` (`emptyOutDir: true`). Must complete before the backend starts so served assets aren't stale. If this fails, stop — do not start the servers.

### 4. Start the Vite dev server (background)

```bash
BACKEND_PORT=$BACKEND_PORT npm --prefix zeus-web run dev -- --port $FRONTEND_PORT --strictPort
```

- Run with `run_in_background: true` on the Bash tool.
- `BACKEND_PORT` tells the Vite proxy where to forward `/api` and `/ws`.
- `--strictPort` makes Vite fail loudly rather than silently picking another port.

### 5. Start the .NET backend (background)

```bash
ZEUS_PORT=$BACKEND_PORT dotnet run --project Zeus.Server
```

- Run with `run_in_background: true`.
- `ZEUS_PORT` is read in `Zeus.Server/Program.cs` to drive `ListenAnyIP`. No source edits required.

### 6. Verify both ports are listening, then report

Give each server a moment, then probe:

```bash
lsof -iTCP:"$FRONTEND_PORT" -sTCP:LISTEN -P | tail -n +2
lsof -iTCP:"$BACKEND_PORT"  -sTCP:LISTEN -P | tail -n +2
```

If either port has no listener after ~10 seconds, read the background task output and report the failure honestly — don't claim success.

Final message must name the ports explicitly, e.g.:

```
Zeus is running:
  Vite dev:  http://localhost:<FRONTEND_PORT>   (proxies /api,/ws → :<BACKEND_PORT>)
  Backend:   http://localhost:<BACKEND_PORT>
  wwwroot:   built from zeus-web into Zeus.Server/wwwroot
```

## Do NOT

- Do **not** edit `Program.cs`, `vite.config.ts`, or any other source file — the env-var plumbing is already in place.
- Do **not** run tests or a separate `dotnet build` — `dotnet run` compiles and step 3 already builds the frontend.
- Do **not** foreground either server — both must be backgrounded so control returns to the user.
- Do **not** start the backend before the frontend build completes, or `wwwroot` may be stale/empty.
