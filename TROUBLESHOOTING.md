# Troubleshooting Log

Real failures hit while containerizing this stack, what each one actually meant, and how to recognise it faster next time.

Organised by **symptom**, because that's the only thing you have when something breaks.

---

## The fastest diagnostic: read the clock before the error

Most connection failures announce where the fault lives through **how long they take**, before you read a single log line.

| What you see | Elapsed | What it means |
|---|---|---|
| `curl: (7) Could not connect` | instant | Nothing is published on that host port. No door there at all. |
| `curl: (52) Empty reply from server` | ~5 ms | Host port is published, but nothing is listening on the **container** port behind it. Door opens onto an empty room. |
| `500 Internal Server Error` | ~4 s | App reached, but *its* dependency never answered. DNS didn't resolve or the service isn't running. |
| `500 Internal Server Error` | ~20 ms–1.4 s | App reached, dependency reached, dependency said no. Schema, permissions, or query problem. |

**The rule:** a fast failure means you reached something and it refused. A slow failure means you never reached anything. Latency tells you which side of the connection the fault is on.

First call to a database is slower than subsequent ones (~1.4s vs ~20ms) because of connection pool setup and authentication. Don't mistake first-call overhead for a network problem.

---

## The second-fastest diagnostic: check the CREATED column

**Caught me twice.** Both times the code was correct and the change simply wasn't running.

A container is a copy made from an image **at start time**. Rebuilding the image does nothing to a container that is already running — the old copy keeps running the old code.

```powershell
docker compose ps    # CREATED says "30 minutes ago" → your change is not in there
```

**Occurrence 1 (M3.2):** Commented out `try_files` in `nginx.conf`, rebuilt, and deep routes still worked. The old `web-test` container was still bound to port 3000 and answering every request.

**Occurrence 2 (M4.1):** Added `USER` to both Dockerfiles, and `id` still reported `uid=0(root)`. `docker compose up --build` had been run from the project subfolder instead of the repo root, so `compose.yaml` was never found.

**Rules that prevent both:**

- Run every `docker compose` command from the **repo root**, where `compose.yaml` lives.
- After `up -d --build`, look for `Recreated` in the output — not `Running`.
- `docker compose ps`: CREATED should read *seconds* ago, not minutes.
- A raw `sha256:` digest in the IMAGE column instead of a name means images were rebuilt without containers being replaced.
- `docker compose up -d --build` handles this correctly. A manual `docker run` does **not** — it uses whatever is tagged, and leaves any existing container alone.

---

## Read-only filesystem and tmpfs — three error codes, three different causes

Adding `read_only: true` produced three distinct failures in sequence. They look similar and are not.

| Error | Means | Fix |
|---|---|---|
| `Read-only file system` | Path isn't writable at all | Add a `tmpfs` entry |
| `13: Permission denied` on a tmpfs path | Writable, but owned by the wrong user | Add `uid=`/`gid=` mount options |
| `2: No such file or directory` | The mount hid what the image built there | Stop relying on build-time dirs under a mount |

### The rule that explains all three

> A tmpfs (or volume) mount **replaces** whatever the image had at that path. Anything you `mkdir`, `chown`, or place there at build time disappears behind the mount at runtime.

**Occurrence 1:** `mkdir() "/var/cache/nginx/client_temp" failed (13: Permission denied)` — `/var/cache/nginx` *was* in tmpfs, so it was writable. But a tmpfs mount arrives as a brand-new empty directory owned by root, and the Dockerfile's `chown -R 101:101` had applied to the image directory now hidden underneath.

```yaml
    tmpfs:
      - /var/cache/nginx:uid=101,gid=101
```

**Occurrence 2:** `open() "/tmp/nginx/nginx.pid" failed (2: No such file or directory)` — the Dockerfile did `mkdir -p /tmp/nginx`, then `tmpfs: - /tmp` mounted an empty filesystem over `/tmp`. The directory still exists in the image; nothing can reach it.

Fix was to stop needing the subdirectory at all:

```dockerfile
 && sed -i 's|^pid .*|pid /tmp/nginx.pid;|' /etc/nginx/nginx.conf
```

**Note:** `can not modify /etc/nginx/conf.d/default.conf (read-only file system?)` is informational only — an entrypoint script trying to add an IPv6 listener. nginx starts fine without it.

**The nginx container broke three separate times** during hardening (port conflict, PID file, cache permissions) while the .NET API broke zero times. nginx does substantially more filesystem work at startup, so it feels every restriction. The tighter you lock something down, the more you learn about what it was quietly doing.

---

## `Exited (137)` with no error in the logs

**Cause:** the kernel's OOM killer terminated the process for exceeding its cgroup memory limit. 137 = 128 + 9 = SIGKILL.

**Why the logs are empty:** SIGKILL cannot be caught or handled. The process was terminated mid-execution and had no opportunity to log anything, run a shutdown handler, or produce a stack trace. **The silence is the signature.**

**Confirm from the host, not the container:**

```powershell
docker inspect docker-multitier-wbs-api-1 --format "{{.State.OOMKilled}} {{.State.ExitCode}}"
# → true 137
```

**Reproduced deliberately:** the API's limit was set to 8MiB. `docker stats` showed it idling at 7.895MiB / 8MiB — 98.68%, surviving only because .NET reads `/sys/fs/cgroup/memory.max` and sized its GC heap to fit. Thirty requests pushed it over.

**Symptom in the browser:** the page loads but no data appears. nginx is healthy and serving static files; only `/api/` calls fail. "Site is up but nothing loads" points at a live frontend with a dead backend.

**Sane limits:** enough headroom that normal load never approaches the ceiling, low enough that a runaway leak trips the fuse before it takes the host down. At 256M the API idles at a few percent.

---

## Tracing a request across all four legs

Bisecting (testing each leg separately) finds *where* it breaks. Tracing follows one request through. Without request IDs plumbed in, the technique is **three logs, one clock**.

| Leg | Where to look | What it tells you |
|---|---|---|
| Browser → nginx | DevTools Network tab (tick **Disable cache**) | Status, timing, whether the request left the browser at all |
| nginx | `docker compose logs web` | Whether nginx received it and what it returned |
| API | `docker compose logs api` | Whether the request arrived, and any exception |
| Postgres | `docker compose logs db` | Connection attempts and errors — not queries, by default |

**The method:**

```powershell
docker compose logs -f          # leave running in a second window
# then make exactly one request in another window
```

`-f` follows all services live, interleaved with service prefixes. `--since 1m` cuts startup noise.

**Where the trail stops is the broken leg:**

- Nothing in `web` → never reached nginx. Wrong host port, or container down.
- In `web` but not `api` → nginx couldn't reach the API. Wrong upstream, or API dead.
- In `api` with a 500 → the API failed. Read the exception.
- Exception names a host → never reached Postgres.
- In `db` with an error → reached Postgres and Postgres refused.

**To confirm the technique works, break it deliberately:** `docker compose stop api`, make the same request, and watch the trail stop one leg earlier.

**If you want real per-request tracing**, add `$request_id` to an nginx `log_format`, forward it with `proxy_set_header X-Request-ID $request_id`, and log the same header in the API. That's the foundation of distributed tracing.

---

## Engine and environment

### `error during connect: ... open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified`

**Cause:** Docker Desktop isn't running. The CLI and the engine are separate processes; the CLI is trying to reach the engine over a named pipe that nothing is listening on.

**Fix:** Start Docker Desktop, wait for the tray whale to stop animating (30–60s while the Linux VM boots).

```powershell
docker version   # needs BOTH a Client and a Server section
```

**Recognise it:** `error during connect` **always** means "can't reach the engine," never "your command was wrong."

---

### PowerShell prompts about script execution when running `curl`

**Cause:** `curl` in PowerShell is an alias for `Invoke-WebRequest`, which parses responses as HTML.

**Fix:** Use `curl.exe` — real curl ships with Windows. Also what you'll run inside containers and in CI, so the habit transfers.

```powershell
curl.exe -i -w "`ntime: %{time_total}s`n" http://localhost:8000/todos
```

`-i` shows the status line, `-w` shows elapsed time — both essential for the timing diagnostic above.

---

### Browser shows 200 or blank status with `0 B` transferred

**Cause:** Chrome served a cached response. The request never reached the API.

**Fix:** Verify from the command line, where nothing caches. A cached 200 masking a broken port cost real debugging time here.

---

## Build failures

### `failed to read dockerfile: open Dockerfile: no such file or directory` with `transferring dockerfile: 2B`

**Cause:** Wrong working directory. The `.` at the end of `docker build` is the **build context** — Docker can only see files inside it.

**Recognise it:** `transferring dockerfile: 2B`. A real Dockerfile is 100+ bytes. Two bytes means it found essentially nothing.

**Fix:** `cd` to the folder containing the Dockerfile.

---

### `node:24-alphine: not found`

**Cause:** Typo (`alphine` → `alpine`).

**Worth knowing:** "not found" here refers to the **registry**, not your disk. The error is identical whether you fat-fingered the name or asked for a version that was never published.

**Recognise it:** Docker echoes the offending line back with its line number:
```
Dockerfile.bad:1
   1 | >>> FROM node:24-alphine
```

---

### `error CS0200: Property or indexer 'IOpenApiMediaType.Example' cannot be assigned to`

**Cause:** Version mismatch between the OpenAPI XML-comment **source generator** and the resolved `Microsoft.OpenApi` version. The generator emitted code that no longer compiles. Likely triggered by a transitive bump when EF Core packages were added.

**Recognise it:** the file path points into `obj/`:
```
obj\Debug\net10.0\...\OpenApiXmlCommentSupport.generated.cs(399,41)
```

**When an error points into `obj/`, the bug is a package or generator mismatch — not your source code.**

**Fix:** Removed the package entirely (not needed for this project — no Swagger in the production container anyway):

```powershell
dotnet remove package Microsoft.AspNetCore.OpenApi
dotnet clean
Remove-Item -Recurse -Force obj, bin
```

The last step is the one people skip. The broken generated file is cached on disk and will keep failing the build until it's deleted.

---

### `Unable to find fallback package folder 'C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages'` during a Linux container build

**The most instructive failure in this project.** The error names a Windows path, inside a Linux container, and looks like a missing package. It is neither.

**Cause:** No `.dockerignore` existed, so `COPY . .` copied the host's `obj/` folder into the container — directly over the `obj/` that `dotnet restore` had just generated inside it. `obj/project.assets.json` records **absolute paths from the machine that restored it**. A Linux SDK then read a file describing a Windows NuGet cache.

**Recognise it:** the build output tells you before the error does:
```
=> [internal] load .dockerignore
=> => transferring context: 2B          ← no .dockerignore exists
=> [internal] load build context
=> => transferring context: 36.04MB     ← host bin/ and obj/ went in
```

**Fix:** Create `.dockerignore` beside the Dockerfile:

```
bin/
obj/
.vs/
*.user
Dockerfile
.dockerignore
.git/
```

Build context dropped from **36.04 MB to under 1 MB**.

**`.dockerignore` is correctness, not hygiene.** Host build artifacts leaking into a container build produce failures that look like dependency problems and aren't. The same class of bug awaits any Node build where a Windows-installed `node_modules` (containing native binaries) gets copied into an Alpine container.

---

### `warning NETSDK1194: The "--output" option isn't supported when building a solution`

**Cause:** The `.slnx` file sat inside the project folder and got copied into the build context. `dotnet publish` with no explicit target found the solution and built that instead of the project.

**Fix:** Name the project explicitly. Good practice regardless — a build that relies on `dotnet publish` guessing correctly breaks the day someone adds a second project.

```dockerfile
RUN dotnet publish MultiTierApplication.csproj -c Release -o /app/publish --no-restore
```

---

## Runtime and networking

### `curl: (52) Empty reply from server` in ~5 ms

**Cause:** Port mapping mismatch. `"8000:8000"` published host port 8000 to **container** port 8000, but the app listens on 8080 (set by `ASPNETCORE_HTTP_PORTS` in the Dockerfile).

**Why it fails quietly:** Docker will happily forward a host port to a container port where nothing is listening. It has no idea what's running inside. The forwarding works; the connection dies on arrival.

**Recognise it:** container shows `Up` in `docker compose ps` but is unreachable. **"Up" only means the process hasn't exited** — not that it's working.

**Fix:** Confirm the port the app actually binds:

```powershell
docker compose logs api | Select-String "Now listening"
# → Now listening on: http://[::]:8080
```

Then either route the door to the right office:
```yaml
ports:
  - "8000:8080"   # host:container
```

or move the office to match the door:
```yaml
environment:
  ASPNETCORE_HTTP_PORTS: 8000
ports:
  - "8000:8000"
```

The first is more conventional — the container port is normally treated as a fixed property of the image.

---

### `Bind for 0.0.0.0:3000 failed: port is already allocated`

**Cause:** A leftover standalone container (`web-test`, from testing the frontend image before adding it to Compose) still held host port 3000. Two services cannot claim the same host port — first one there keeps it.

**Fix:**

```powershell
docker ps -a --filter "publish=3000"    # find the Docker occupant
docker rm -f web-test

netstat -ano | findstr :3000            # if it's not Docker — Node dev servers love 3000
Get-Process -Id <pid>
```

**Worth noting:** this is what a *good* error looks like. Docker refused to start and named the exact conflict, rather than silently choosing another port. Contrast with the empty-reply failure above, which gave no such clue.

---

### `nginx: [emerg] open() "/run/nginx.pid" failed (13: Permission denied)`

**Cause:** After adding `USER 101` to the nginx image, the master process could no longer write its PID file. `/run` is still root-owned.

**The subtlety:** `chown -R 101:101 /var/run` does **not** fix this. In Alpine, `/var/run` is a **symlink to `/run`** — `chown` followed the link and changed the target's ownership entry, not the directory contents nginx actually needs.

**Fix — narrow the permission rather than widen it:**

```dockerfile
RUN chown -R 101:101 /var/cache/nginx /etc/nginx/conf.d \
 && mkdir -p /tmp/nginx && chown 101:101 /tmp/nginx \
 && sed -i 's|^pid .*|pid /tmp/nginx/nginx.pid;|' /etc/nginx/nginx.conf
```

Relocating the PID file to a user-owned directory beats `chown -R /run`, which would grant write access to an entire system directory to make one file writable. It also survives a read-only root filesystem, where `/run` would break again.

**Also in that log:** `the "user" directive makes sense only if the master process runs with super-user privileges, ignored`. Harmless — nginx's config asks to drop workers to the nginx user, which needs root to do. The warning is actually confirmation that the `USER` change took effect.

---

### `exec: "ping": executable file not found in $PATH`

**Not a fault.** The runtime image contains the .NET runtime and application DLLs, and nothing else. No ping, no curl, no networking tools. Slim images are slim because things were left out.

**Fix:** Use a throwaway container with tools attached to the same network:

```powershell
docker run --rm --network docker-multitier-wbs_default alpine ping -c 3 db
```

**This gets sharper later.** A chiseled base image has no shell at all — `docker exec sh` is impossible. Debugging then depends on structured logging, health checks that don't assume `curl` exists, and ephemeral debug containers.

---

### `curl: (6) Could not resolve host: real` when POSTing JSON

**Cause:** PowerShell quoting. It split the inline JSON body on spaces and handed curl the fragments as separate arguments. The API received a malformed body and correctly returned `400`; the leftover fragments became garbage commands.

**Fix — write the body to a file:**

```powershell
'{"title":"first real todo","done":false}' | Set-Content -Encoding ascii todo.json
curl.exe -i -X POST http://localhost:8000/todos -H "Content-Type: application/json" -d "@todo.json"
```

`-Encoding ascii` avoids a byte-order mark, which .NET's JSON parser rejects. Or use `Invoke-RestMethod`, which serialises properly.

**Why the file approach is the better habit:** the CI smoke test runs on Ubuntu with bash, where quoting rules differ completely. A command that works in PowerShell can fail there and vice versa. A file sidesteps both shells.

---

### `api` service accidentally builds the migrator image

**Cause:** Compose builds the **last stage** in a Dockerfile by default. Adding the `migrator-build` and `migrator` stages after the `runtime` stage silently changed which stage that was.

**Fix:** Name the target explicitly once a Dockerfile has more than one final-candidate stage:

```yaml
  api:
    build:
      context: ./src/api/MultiTierApplication
      target: runtime
```

---

### `docker compose down` then `up` still runs the old image

**Cause:** `down` removes **containers**, not images. `up` without `--build` happily reuses whatever image is already tagged, so Dockerfile edits are ignored.

**How it showed up:** after switching the API's base image to chiseled, `docker compose exec api sh` still returned a working `$` prompt. A chiseled image has no shell at all — so the old image was clearly still running.

**Fix:**

```powershell
docker compose build api      # run alone and read the output
docker compose up -d
docker images docker-multitier-wbs-api   # size confirms which image is live
```

Third variation of the same trap, after the `try_files` test and the `USER` directive. The general form: **a change only takes effect once both the image is rebuilt and the container is replaced.**

---

## Debugging a container with no shell

The chiseled base image ships the .NET runtime and application DLLs — no shell, no package manager, no utilities.

```
docker compose exec api sh
→ exec: "sh": executable file not found in $PATH
```

That error **is** the security feature. Three techniques replace `exec`:

**1. Logs.** `docker compose logs <service>` solved four separate failures during hardening. Structured logging matters more as images get thinner — the container can only tell you what you arranged for it to say.

**2. Inspect from the host.** `docker inspect`, `docker compose ps -a`, `docker stats`, `docker diff` all query the engine, not the container. The container's poverty is irrelevant. This is how the OOMKill was confirmed.

**3. Bring your own tools.** A throwaway container with utilities, attached to the same network:

```powershell
docker run --rm --network docker-multitier-wbs_default alpine ping -c 3 db
```

In Kubernetes the same idea is `kubectl debug` — an ephemeral container sharing the target pod's namespaces.

**The mindset shift:** stop treating containers as small machines to log into; treat them as processes to observe from outside. Which is how you'd debug at scale anyway — nobody SSHes into 200 pods.

---

### `db` missing from `docker compose ps` after editing `compose.yaml`

**Cause:** Either the file wasn't saved, indentation was wrong, or `docker compose up` hadn't been re-run.

**Fix:** `docker compose config` prints the fully-resolved file. If a service isn't in that output, it isn't in your file. Check that `db:` sits at the same indent level as `api:`, and that top-level `volumes:` is flush-left with `services:`.

---

### Postgres version mismatch against an existing volume

**Cause:** `postgres:15` was used instead of the intended `postgres:16-alpine`. Postgres data directories are version-specific — pointing a v16 server at a volume initialised by v15 fails.

**Fix:** With throwaway data, wipe the volume:

```powershell
docker compose down -v
docker compose up -d
```

**With real data this is a migration, not a wipe.** `pg_upgrade` or dump-and-restore.

**Why it matters here:** version parity between environments is the entire point of containerizing. "It worked on 15 in dev, prod runs 16" is exactly the class of bug this project exists to prevent.

---

## Git and Visual Studio

### `git add .` → `.vs/...vsidx: Permission denied`

**Cause:** Visual Studio holds file locks on `.vs/` (its private index and editor state) while open. It should never be tracked.

**Fix:** Add `.vs/` to `.gitignore`. If it was already staged: `git rm -r --cached .vs`. If it persists, close Visual Studio.

---

### `git status` shows loose `.cs` files with no `src/` prefix

**Cause:** The repo was initialised **inside the project folder** instead of at the repository root. The root `.gitignore` wasn't visible to it, and `.github/workflows/` for CI would have been unreachable entirely.

**Fix (before any commits exist):**

```powershell
Remove-Item -Recurse -Force .git
cd <repo-root>
git init -b main
```

**Same principle as build context:** the boundary you choose determines what's reachable.

---

### Project properties / launch profiles / Manage User Secrets missing from the Visual Studio menu

**Cause:** Solution Explorer is in **Folder View**, which shows raw files on disk. Those are project-level commands and only exist in Solution View.

**Fix:** Use the Switch Views button in the Solution Explorer toolbar.

---

### Connection string committed in `launchSettings.json`

**Cause:** Visual Studio's launch profile UI writes environment variables into `Properties/launchSettings.json`, which **is** committed by default.

**Fix:** Use User Secrets instead — stored in the user profile, outside the repo:

```powershell
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Database=postgres;Username=postgres;Password=devpass"
```

Note the **colon** for User Secrets (JSON config) versus the **double underscore** for environment variables (`ConnectionStrings__Default`) — env var names can't contain colons on all platforms. Both feed the same `GetConnectionString("Default")`; the app can't tell the difference.

**Caught before the first commit.** After a commit — especially a pushed one — removing a secret means rewriting history.

---

### `relation "todos" does not exist` in psql, when the table clearly exists

**Cause:** EF Core creates the table as `Todos` with a capital T. Postgres folds unquoted identifiers to lowercase, so `SELECT * FROM Todos` looks for `todos`.

**Fix — quote the identifier, escaped for PowerShell:**

```powershell
docker compose exec db psql -U postgres -d todos -c "SELECT * FROM \"Todos\";"
```

---

### A config key that silently does nothing

**Found while cleaning `launchSettings.json`:** it contained `ConnectionStrings__DefaultConnection`, but the code reads `GetConnectionString("Default")` — which maps to `ConnectionStrings__Default`.

**The key had never worked.** Local runs were reading from User Secrets the whole time. No error, no warning; .NET's configuration system simply found nothing at that key and moved on to the next provider.

**Why it's a nasty class of bug:** layered configuration means a misspelled key in one provider is invisible as long as another provider supplies the right value. You only discover it when you remove the provider that was actually doing the work — often in a different environment, at deploy time.

**Guard:** fail fast on startup if a required config value is missing, rather than letting it default to null.

---

### `error: cannot spawn git-filter-repo: Permission denied`

**Cause:** on Windows, pip installs `git-filter-repo` as an extensionless Python script that the OS can't execute directly.

**Workaround:** `python -m git_filter_repo --replace-text replacements.txt`

**What was done instead:** `git rebase -i --root`, marking the offending commit as `edit`, then `git rm --cached` the file and `git commit --amend`.

**The lesson that cost the most time:** the rebase removed the credential from `launchSettings.json` in commit one, but `git log -p --all -S "devpass"` afterwards still returned **three more hits** — the same password had been written into `compose.yaml` and carried forward through five subsequent commits. **Check the full scope of a leak before choosing a removal strategy**, not after.

**Outcome:** the credential was assessed as non-exploitable (localhost only, no published host port, superseded by `.env`), and the history was left as-is rather than rewritten further. A `.gitleaksignore` entry documents the reasoning.

---

### OneDrive interfering with git and Docker

The repo lives on a synced business OneDrive path. Three separate incidents:

- `git add .` → `.vs/...vsidx: Permission denied` (Visual Studio locks, compounded by sync)
- `git rebase -i --root` → four `Deletion of directory ... failed` prompts mid-rebase
- `git rebase --continue` → `could not remove '.git/rebase-merge'`, leaving git believing a rebase was still in progress

**Fix for the last one:**

```powershell
Remove-Item -Recurse -Force .git\rebase-merge
git status    # confirm no rebase in progress
```

Do **not** run `git rebase --continue` at that point — the rebase had already succeeded, and continuing risks replaying commits.

**Mitigation:** close Visual Studio and pause OneDrive sync before any history rewrite. Better: keep repositories outside synced folders entirely.

---

## Breakage drill 1 — three injected faults, diagnosed cold

Three faults were injected into `compose.yaml` without being disclosed. All three were found from evidence. **None was a bug in application code** — each was two places that had to agree and didn't.

### Fault 1 — health check probing the wrong port

**Symptom:** `docker compose up -d` → `dependency failed to start: container docker-multitier-wbs-db-1 is unhealthy`. Nothing else started.

**Evidence:**

```powershell
docker inspect docker-multitier-wbs-db-1
# → {"ExitCode": 2, "Output": "/var/run/postgresql:5433 - no response"}

docker compose logs db
# → listening on IPv4 address "0.0.0.0", port 5432

docker exec -it docker-multitier-wbs-db-1 sh
# pg_isready → /var/run/postgresql:5432 - accepting connections
```

**Hypothesis:** the health check was probing 5433 while Postgres bound 5432.

**Root cause:** `healthcheck.test` specified `-p 5433`. Confirmed by four independent sources agreeing.

**Fix:** change to 5432.

**Two lessons.** Postgres was healthy the entire time — listening, accepting connections, completely fine. **The check was wrong, not the service.** A monitoring failure that presents as a service failure. When something reports unhealthy, "is the check correct?" is a legitimate hypothesis alongside "is the service broken?"

And because `api` and `web` both `depends_on` db being healthy, this fault **masked the other two entirely**. Layered faults have to be peeled in dependency order.

### Fault 2 — published port pointing at nothing

**Symptom:** `curl: (52) Empty reply from server` on the proxy path; browser at `localhost:3000` dead.

**Evidence:** cross-referenced three files — `compose.yaml` published `${WEB_PORT}:8081`, `nginx.conf` had `listen 8080`, the Dockerfile had `EXPOSE 8080`.

**Root cause:** two of three agreed on 8080, so the odd one out was the fault.

**Fix:** `${WEB_PORT}:8080`.

Same `(52)` signature as the earlier port mismatch, same underlying cause: a door wired to a room nobody is in.

### Fault 3 — hostname that doesn't resolve

**Symptom:** `HTTP/1.1 500` on `/todos`, **after 5.7 seconds**.

**Evidence:** connection string read `Host=database`; the compose service is named `db`.

**Root cause:** the name doesn't resolve, so the connection attempt times out.

**Fix:** `Host=db`. After the fix: **200 in 1.7s** (first-call pool setup), then double-digit milliseconds.

**This confirmed the timing rule quantitatively.** The broken case took 5.7s rather than the 4.08s seen earlier — DNS timeout varies with resolver config and machine load, so the absolute number moves. What is stable is the **order of magnitude**:

> **Seconds → you never reached it.** DNS, networking, service not running, wrong port.
> **Milliseconds → you reached it and it refused.** Schema, permissions, bad query, credentials.

---

## Reading Postgres startup logs

Three lines worth recognising:

**`PostgreSQL Database directory appears to contain a database; Skipping initialization`** — the volume has existing data, so no fresh init. Correct and expected on every start after the first. Postgres only initialises a data directory it finds **completely empty**.

**`database system was shut down at <time>`** followed by `ready to accept connections` — a clean shutdown and recovery. If the container had been killed hard, you would instead see recovery messages about replaying the write-ahead log. Useful when a database comes back slowly: clean vs crash recovery.

**`checkpoint starting: time` / `checkpoint complete`** — routine buffer flushing on a timer. This is the mechanism that makes the volume durable rather than just holding what was in memory.

**A note on named volumes vs bind mounts:** `docker volume rm` followed by `docker volume create` produces an empty named volume, which is exactly what Postgres expects on first run — it initialises normally. The failure mode where Postgres refuses to start because it finds a non-empty directory that isn't a valid data directory applies to **bind mounts** pointed at a host folder containing stray files, not to named volumes.

---

## CI pipeline — what broke and what it taught

### `No such image: docker-multitier-wbs-api:latest` in Trivy, after a successful build

**Cause:** Compose derives the project name — and therefore image names — from the **directory name**. Locally the folder is `docker-multitier-wbs`; on the runner, `actions/checkout` clones into a directory named after the repo (`multi-tier-application`). The images built fine, under a different name.

**Fix — pin the project name explicitly** at the top of `compose.yaml`:

```yaml
name: docker-multitier-wbs
```

Implicit naming derived from a directory is exactly the environment-dependent behaviour containers exist to eliminate. Namespaces in Kubernetes behave similarly.

**Guard:** add a `docker images` step before any scan, so a future mismatch is obvious rather than cryptic.

---

### Trivy blocked the build on a HIGH CVE in the base image

**Finding:** `CVE-2026-14456` in `libcrypto3` / `libssl3` at `3.5.7-r0`, in `nginx:alpine`. Status `fixed`, `Fixed Version: 3.5.8-r0` — a patch existed upstream.

**First attempt:** `docker compose build --pull` to force a fresh base image pull rather than reusing a cached layer. **Did not help** — Alpine had published the patched package, but the nginx image had not yet been rebuilt to include it.

**Three options, all defensible:**

1. **Patch it yourself** — `RUN apk upgrade --no-cache libssl3 libcrypto3` in the runtime stage, before `USER 101` (apk needs root).
2. **Assess and accept** — the CVE is a DoS in the **QUIC server**; this nginx serves HTTP/1.1 over a bridge network with no public exposure. Arguably not reachable. Document it and lower `severity` to `CRITICAL`.
3. **Wait** for the base image rebuild. Correct with a patching SLA; useless on a deadline.

**Chose 1.** It actually fixes the issue and needs no argument about reachability. Verified locally before pushing:

```powershell
docker compose exec web apk list --installed | Select-String "libssl3|libcrypto3"
# → 3.5.8-r0
```

**What is not acceptable:** silently removing `exit-code: '1'`. That turns off the alarm rather than triaging the finding — the security equivalent of `curl` without `-f`.

**The API image passed clean.** Chiseled removed nearly everything a scanner looks at. A concrete result from the base-image decision, separate from the size saving.

---

### Gitleaks reported "no leaks found" on a repository with a known credential

**The best finding of the project**, and it took two separate discoveries.

**Discovery 1 — it was only scanning one commit.** Buried in the debug output:

```
gitleaks cmd: gitleaks detect ... --log-opts=-1
INF 1 commits scanned.
```

`gitleaks-action` defaults to `--log-opts=-1` on push events — the latest commit only. "No leaks detected" meant "the commit you just pushed is clean," not "this repository is clean."

**The action accepts no inputs for this.** Passing `with: args:` fails with `Unexpected input(s) 'args', valid inputs are ['']`. Running the binary directly makes the scope explicit:

```yaml
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0        # without this the runner has a shallow clone — nothing to scan

      - name: Scan for secrets
        run: |
          docker run --rm -v "${{ github.workspace }}:/repo" \
            zricethezav/gitleaks:latest detect \
            --source /repo --log-opts=--all --redact -v
```

Result: **20 commits scanned.**

**Discovery 2 — it still found nothing.** But the credential is provably there:

```powershell
git log -p --all -S "devpass" | Select-String "devpass"
# → four hits across compose.yaml and launchSettings.json
```

**A false negative.** Gitleaks' default rules target high-entropy strings and recognisable credential formats — AWS keys, GitHub tokens, private keys. `Password=devpass` is a short dictionary word in a YAML file and matches no pattern.

**The lesson, which generalises well beyond Gitleaks:**

> Scanners catch what they are built to catch. Default rulesets are tuned to minimise false positives, which necessarily means missing things. A green check is **not** proof of absence — it is a net with a specific mesh size.

A custom rule in `.gitleaks.toml` could catch this specific case. The more durable habit is verifying that a control actually fires before trusting it.

---

### Verifying the pipeline can actually fail

A pipeline that has only ever passed is untested — you cannot tell whether it is green because the code is good or because the checks are not really checking.

**Method:** branch, break `/healthz` to return `Results.StatusCode(500)`, open a PR, watch it go red.

**Result:** failed at "Verify API health" with `curl: (22)` and exit code 22.

**Two things this exposed:**

`-fsS` on curl is load-bearing. Without `-f`, curl receives a 500, prints it, and **exits 0** — green pipeline, broken app.

**The failure was caught later than it should have been.** `docker compose up -d --wait` blocks on health checks, but the API has no `healthcheck` defined in `compose.yaml`, so `--wait` had nothing to wait on and reported success. Adding one would catch a broken API a step earlier and make `--wait` meaningful for that service.

Same principle as "an untested backup is not a backup."

---

### `Node.js 20 is deprecated` annotation

Not a failure. GitHub is retiring the Node 20 runtime that some actions declare, and is running them on Node 24 via a compatibility shim. Bump the action major versions when replacements exist — but a deprecation warning is better than pinning to a version that does not exist.

---

## Commands worth memorising

```powershell
docker compose config                        # resolve and validate without running
docker compose ps                            # STATUS "Up" ≠ working; CREATED confirms your change landed
docker compose ps -a                         # includes exited containers — the migrator, and anything that crashed
docker compose logs api --tail 20            # where the real error lives
docker compose logs -f                       # follow all services live, interleaved
docker compose logs --since 1m               # skip startup noise
docker compose logs api | Select-String "Now listening"   # the true container port
docker stats --no-stream                     # actual usage against your limits
docker inspect <container> --format "{{.State.OOMKilled}} {{.State.ExitCode}}"
docker images <image>                        # size confirms which image is actually live
docker volume ls                             # what survives `down`
docker run --rm --network <net> alpine ...   # borrow tools a slim image doesn't have
curl.exe -i -w "`ntime: %{time_total}s`n"    # status + elapsed, the core diagnostic
git log -p --all -S "<secret>" | Select-String "<secret>"   # full scope of a leak
```

---

## Things that cost time and were avoidable

- **Repo lives inside a synced OneDrive folder.** OneDrive syncs `bin/`/`obj/` churn constantly, can lock files mid-build, and can corrupt `.git` during a sync. Unexplained file-lock or `index.lock` errors: suspect this first.
- **`Content-Length: 0` on a 500** is ASP.NET in Production mode correctly declining to leak stack traces to callers. The useful detail stays in the container logs — which is why structured logging matters more once the browser stops telling you anything.
- **Checking the browser instead of the command line.** Cached responses masked a genuinely broken port mapping for several minutes.
