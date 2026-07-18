---
name: app-snap
description: Use whenever you need to SEE the running sample app — screenshots for the user, UI verification of a change, or driving a flow with Playwright (login, tenant picker, records, dialogs). Wraps the login traps and locator gotchas so you don't rediscover them.
---

# Driving and screenshotting the sample app

The reusable driver is **`scripts/snap.mjs`** — a CLI for common shots and an importable
library (`launch`, `signIn`) for bespoke flows. Run it from the repo root (playwright
resolves from the workspace; chromium is auto-detected, including the container's
`/opt/pw-browsers` build).

## Getting a server worth photographing

1. Build the SPA into the host: `cd samples/web && npx vite build` (outputs to
   `samples/erp/wwwroot`).
2. Serve EVERYTHING from the API origin — screenshot against `http://localhost:5100`,
   never the vite dev server: `:5173` only proxies `/api`, so the OIDC login redirect
   dead-ends there.
3. Start the host on a fresh SQLite DB (check the port is free first — `curl -s -o
   /dev/null -w "%{http_code}" http://localhost:5100/ --max-time 2` must print `000`):

   ```sh
   rm -f /tmp/erp-fresh.db*
   ConnectionStrings__erp="Data Source=/tmp/erp-fresh.db" dotnet run --project samples/erp &
   ```

   Seeded data is thin; running `node verify/all.mjs` first populates orders, folders,
   shares and rules — good screenshot material (but that DB is then "used": don't reuse it
   for another verification round).

## CLI quickstart

```sh
node scripts/snap.mjs --shot /tmp/landing.png
node scripts/snap.mjs --click "Ordrar" --row 0 --tab "Dokument" --shot /tmp/order-docs.png
node scripts/snap.mjs --path "?mode=admin" --shot /tmp/admin.png
node scripts/snap.mjs --user tekla --tenant "Demo AB" --click "Arbetsordrar" --shot /tmp/tech.png
```

Flags `--base --user --password --tenant` configure; `--path` navigates after sign-in
(the URL grammar is `?mode=&page=&record=&tenant=&tab=` — docs/32); step flags
(`--click --button --row --tab --wait --shot`) run in the order given.

## The login flow (what signIn() absorbs)

1. Landing page = one "Logga in" button; the credential FORM is the server-rendered
   authorize page behind it.
2. Seeded usernames are not email-shaped — the form needs `novalidate` set before filling,
   or the email input blocks submit.
3. A tenant picker may follow: click the company name (use `{ exact: true }` — subtree
   labels like "Demo AB ▸ Norrservice Nord AB" also contain "Demo AB"), then "Fortsätt".

## Locator gotchas (bespoke flows)

- The header company picker is `header input[class*="Select"]` — `header input` alone
  matches the language SegmentedControl radios.
- Inputs inside modals: scope to the dialog, `page.locator('[role=dialog]')
  .getByRole('textbox').first()` — `getByLabel` is often ambiguous with the page behind.
- ALWAYS `await browser.close()` in `finally` — a hanging chromium keeps node alive until
  the outer command times out.
- Kill a stuck script with a pkill matching ONLY the script name, as its own command —
  never inside a compound command (pkill's exit 144 aborts everything after it).

## Users worth knowing (password `demo123`, tenant "Demo AB")

- `alva` — admin (`*`): sees every mode incl. Administration + Utvecklare.
- `tekla` — technician: work/field modes, own-scoped records, no admin.
- `didrik` — dispatcher.
