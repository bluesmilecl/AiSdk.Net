# Publishing to NuGet with Trusted Publishing (OIDC)

> 🇪🇸 [Leer en español](nuget-trusted-publishing.es.md)

Applied with the `dotnet-nuget:nuget-trusted-publishing` skill from [dotnet/skills](https://github.com/dotnet/skills).

`.github/workflows/release.yml` no longer uses a long-lived API key. When a `vX.Y.Z` tag is pushed,
the job requests an OIDC token from GitHub (`permissions: id-token: write`), the `NuGet/login@v1` action exchanges it
on nuget.org for a temporary API key (≈1 hour), and `dotnet nuget push` uses it.

## One-time setup

1. **nuget.org** → your account → *Trusted Publishing* → *Add policy*:
   | Field | Value |
   |---|---|
   | Repository owner | `bluesmilecl` |
   | Repository | `AiSdk.Net` |
   | Workflow file | `release.yml` (file name only, without `.github/workflows/`) |
   | Environment | `release` |

   The policy owner must be the same user or organization that owns the `AiSdk.Net.*` packages (on the first publish,
   new packages are owned by that owner).

   > [!IMPORTANT]
   > If the repository is **private**, the policy is only active for **7 days** until the first successful publish (that
   > publish locks the repository and owner IDs and makes the policy permanent). If the window expires, restart it from
   > the same policy.
2. **GitHub** → *Settings* → *Environments* → create `release` (optional: approval rules or a restriction to `v*` tags).
3. In that environment, create the `NUGET_USER` secret with the nuget.org **profile name** (not the email address).

## Verification

1. Create a test tag with a prerelease version, e.g. `git tag v0.1.1-preview.1 && git push origin v0.1.1-preview.1`.
2. Confirm in Actions that the *NuGet login (OIDC)* step succeeds and that the packages show up on nuget.org.
3. Only after a successful publish, revoke the old API key on nuget.org and delete the `NUGET_API_KEY` secret from GitHub.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `Unable to get ACTIONS_ID_TOKEN_REQUEST_URL` | `id-token: write` is missing from `permissions`. |
| `403` / policy not found | Owner, repo, workflow name or environment do not exactly match the policy. |
| `401` on push | `NUGET_USER` is the email address, or a user without permissions on the packages. |
| The package already exists | `--skip-duplicate` avoids failing when the same tag is retried. |
