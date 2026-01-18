# StripePractice

> ⚠️ Work-in-progress and built for my own testing—some endpoints may be rough or experimental, so expect occasional oddities.

End-to-end Stripe subscription demo for ASP.NET Core 9.0. Includes customer creation, subscription lifecycle (create, confirm, cancel, change plan), prorated annual billing, and webhook handling that keeps the local Postgres database in sync.

## What this covers
- API-first Stripe flows: create customer, start subscription, confirm payment, cancel at period end or immediately.
- Plan management: preview upgrades/downgrades with proration, apply plan changes, annual with-proration flow, subscription type check.
- Webhooks: handles subscription, invoice, charge, and payment intent events to update users and send emails.
- Data layer: Postgres via EF Core with migrations included.
- Static samples under `wwwroot/` for quick client-side testing.

## Prerequisites
- .NET 9 SDK
- PostgreSQL running locally
- Stripe account with test keys and prices
- Stripe CLI (for local webhook forwarding)

## Quick start
1) Install tools and restore packages:
```
dotnet restore
```
2) Configure connection string and Stripe keys in `appsettings.Development.json` (or user-secrets). Do **not** commit real secrets.
3) Apply migrations:
```
dotnet ef database update
```
4) Run the API:
```
dotnet run
```
5) Browse Swagger at `https://localhost:{HTTPS_PORT}/swagger`.

## Configuration
Update the following settings (development or production as appropriate):
- `ConnectionStrings:Default` = `Host=localhost;Port=5433;Database=stripe_use_example;Username=postgres;Password=YOUR_DB_PASSWORD`
- `Stripe:ApiKey` = `sk_test_xxx`
- `Stripe:PublishableKey` = `pk_test_xxx`
- `Stripe:WebhookSecret` = value returned from Stripe CLI when listening locally.

### Getting the webhook secret locally
Run Stripe CLI and copy the `whsec_...` key from the output, then set it in configuration:
```
stripe listen --forward-to https://127.0.0.1:{HTTPS_PORT}/stripe/webhook --latest
```
Replace `{HTTPS_PORT}` with your Kestrel HTTPS port (see `Properties/launchSettings.json`).

## Key endpoints
- `POST /api/stripe/customer` – create Stripe customer for an email (auto-creates local user with trial).
- `POST /api/stripe/subscription` – create subscription for a price and payment method.
- `POST /api/stripe/subscription/pay` – confirm payment for incomplete subscriptions.
- `POST /api/stripe/subscription/cancel` – cancel now or at period end.
- `POST /api/stripe/subscription/preview-change` – preview proration for plan change.
- `POST /api/stripe/subscription/change-plan` – apply upgrade/downgrade.
- `GET /api/stripe/subscription-type/{priceId}` – inspect price billing interval.
- `POST /api/stripe/subscription/annual-with-proration` – annual subscription with prorated amount.
- `POST /stripe/webhook` – Stripe webhook handler (set `Stripe:WebhookSecret`).

## Testing tips
- Use Stripe test cards (e.g., 4242 4242 4242 4242 with any future date and CVC).
- For 3DS flows, use 4000 0027 6000 3184 and confirm via Stripe test modal.
- Reset monthly usage counters by paying a new subscription cycle invoice.

## Notes
- Keep secrets out of source control; prefer environment variables or user-secrets in development.
- The project is still in progress; some endpoints may change or need fixes—verify flows before shipping.
- If migrations fall behind, regenerate from the latest models before pushing.
