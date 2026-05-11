# Tamp.AdoServiceConnection

End-to-end orchestrator for **Workload Identity Federation** Azure
Resource Manager service connections in Azure DevOps. Composes
[`Tamp.AzureCli.V2`](https://github.com/tamp-build/tamp-azure-cli)
and [`Tamp.AdoRest.V7`](https://github.com/tamp-build/tamp-ado-rest)
— the wrapper Strata hand-rolled twice (test + prod) before this
existed.

```csharp
using Tamp.AdoServiceConnection.V1;
```

| Package | Status |
|---|---|
| `Tamp.AdoServiceConnection.V1` | preview |

Requires `Tamp.Core ≥ 1.0.5`, `Tamp.AzureCli.V2 ≥ 0.1.0`,
`Tamp.AdoRest.V7 ≥ 0.1.0`.

## What it does

Walks the 6 steps that hand-rolled scripts always trip over:

1. `az ad app create` — Entra application registration.
2. `az ad sp create` — service principal for the app.
3. `az role assignment create` — scope grant (Contributor at RG scope by default; configurable).
4. ADO REST POST `/_apis/serviceendpoint/endpoints` — creates the service connection. **Captures the WIF subject** ADO generates.
5. (no separate step) — the subject from step 4 feeds step 6.
6. `az ad app federated-credential create` — trusts the ADO-issued OIDC token.

Result is a single record carrying every ID and the WIF subject —
enough to wire up follow-up automation (additional role scopes,
audit logs, lifecycle teardown).

## Build-script example

```csharp
using Tamp;
using Tamp.AdoServiceConnection.V1;

[NuGetPackage("az", UseSystemPath = true)]
readonly Tool AzTool = null!;

[Secret("ADO PAT (Service Connections: Read & Manage)", EnvironmentVariable = "ADO_PAT")]
readonly Secret AdoPat = null!;

[Parameter("Target environment")]
readonly string Env = "test";

Target CreateServiceConnection => _ => _.Executes(async () =>
{
    var sc = await AdoServiceConnection.CreateWifAzureRmAsync(AzTool, new WifAzureRmRequest(
        AdoOrgUrl: "https://dev.azure.com/i3solutions/",
        AdoPat: AdoPat,
        Project: "Strata",
        Name: $"sp-strata-cicd-{Env}",
        SubscriptionId: SubId,
        SubscriptionName: $"Strata {Env}",
        TenantId: TenantId,
        ResourceGroup: $"rg-strata-{Env}",
        RoleDefinitionName: "Contributor"));

    Console.WriteLine($"App ID:         {sc.AppId}");
    Console.WriteLine($"SP Object ID:   {sc.ServicePrincipalObjectId}");
    Console.WriteLine($"ADO SC ID:      {sc.ServiceConnectionId}");
    Console.WriteLine($"WIF Subject:    {sc.WifSubject}");
    // Persist these — adding more role scopes later doesn't need to
    // re-create the SC, just `az role assignment create` against the SP.
});
```

## Prerequisites

The caller is expected to have authenticated `az` first (interactive
login, SPN, managed identity, or another WIF flow). The wrapper does
not invoke `az login` — auth context is the caller's responsibility.

The ADO PAT needs **Service Connections (Read & Manage)** scope.
Typed as `Secret`, joins the redaction table.

## Failure semantics

v0.1.0 throws and **does not roll back partial state**. If step 3
(role assignment) fails after step 1 (app create) and step 2 (SP
create) have succeeded, the app and SP remain in Entra. The
exception message identifies which step failed; cleanup is manual:

```bash
az ad sp delete --id <appId>
az ad app delete --id <appId>
```

Rollback-on-failure is a v0.2.0 candidate.

## What's NOT in v0.1.0

- **Rollback on partial failure** (above)
- **Idempotency** — re-running with the same `Name` against an
  existing app fails on step 1. v0.2.0 candidate: detect existing
  app/SP and skip-or-update.
- **Subscription-scope role grants** — currently only resource-group
  scope. Easy add if there's demand.
- **Custom audiences** beyond `api://AzureADTokenExchange`.

## Releasing

See [MAINTAINERS.md](MAINTAINERS.md).
