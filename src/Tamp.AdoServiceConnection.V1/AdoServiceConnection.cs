using System.Text.Json;
using Tamp.AdoRest.V7;
// Alias the AzureCli facade class — `Tamp.AzureCli` is also a
// namespace (containing the V2 sub-namespace), so a bare `AzureCli`
// reference would resolve to the namespace, not the class.
using Az = Tamp.AzureCli.V2.AzureCli;

namespace Tamp.AdoServiceConnection.V1;

/// <summary>
/// End-to-end orchestrator for creating a Workload Identity Federation
/// Azure Resource Manager service connection in Azure DevOps.
///
/// <para>
/// Walks 6 steps:
/// </para>
/// <list type="number">
///   <item><c>az ad app create</c> — Entra application registration.</item>
///   <item><c>az ad sp create</c> — service principal for the app.</item>
///   <item><c>az role assignment create</c> — scope grant.</item>
///   <item>ADO REST POST <c>/_apis/serviceendpoint/endpoints</c> — service connection. Captures the WIF subject ADO generates.</item>
///   <item>(no separate step) — the result's subject is used in step 6.</item>
///   <item><c>az ad app federated-credential create</c> — trust the ADO-issued OIDC token.</item>
/// </list>
///
/// <para>The caller is expected to have authenticated <c>az</c> first
/// (interactive, SPN, MI, or WIF). The wrapper does not invoke
/// <c>az login</c>.</para>
///
/// <para>On any step failure the orchestrator throws and leaves the
/// partial state in place. v0.1.0 does NOT attempt rollback — that's
/// a feature on the v0.2.0 roadmap. Caller can read the exception's
/// inner message to identify which step failed and clean up manually.</para>
/// </summary>
public static class AdoServiceConnection
{
    /// <summary>Orchestrate the full 6-step flow. Returns the IDs and the federated-credential subject for follow-up automation.</summary>
    public static async Task<WifAzureRmResult> CreateWifAzureRmAsync(
        Tool az,
        WifAzureRmRequest request,
        CancellationToken ct = default)
    {
        if (az is null) throw new ArgumentNullException(nameof(az));
        if (request is null) throw new ArgumentNullException(nameof(request));
        Validate(request);

        var appDisplayName = request.AppDisplayName ?? request.Name;
        var fcName = request.FederatedCredentialName ?? $"{request.Name}-fc";

        // 1. Entra app
        var (appId, appObjectId) = await CreateAdAppAsync(az, appDisplayName, ct).ConfigureAwait(false);

        // 2. Service principal
        var spObjectId = await CreateAdSpAsync(az, appId, ct).ConfigureAwait(false);

        // 3. Role assignment at resource-group scope
        var scope = $"/subscriptions/{request.SubscriptionId}/resourceGroups/{request.ResourceGroup}";
        await CreateRoleAssignmentAsync(az, spObjectId, request.RoleDefinitionName, scope, ct).ConfigureAwait(false);

        // 4. ADO service connection (gives us the WIF subject)
        using var ado = new AdoRestClient(request.AdoOrgUrl, request.AdoPat);
        var sc = await ado.ServiceEndpoints.CreateWifAzureRmAsync(
            project: request.Project,
            name: request.Name,
            subscriptionId: request.SubscriptionId,
            subscriptionName: request.SubscriptionName,
            tenantId: request.TenantId,
            servicePrincipalClientId: appId,
            creationMode: "Manual",
            ct: ct).ConfigureAwait(false);

        var wifSubject = ExtractWifSubject(sc)
            ?? throw new InvalidOperationException(
                $"ADO returned the service connection {sc.Id} without a 'workloadIdentityFederationSubject' parameter. " +
                $"Cannot proceed with federated-credential creation. Manual cleanup of app {appId} and SC {sc.Id} required.");

        // 6. Federated credential on the Entra app
        var fcId = await CreateFederatedCredentialAsync(
            az,
            appObjectId,
            fcName,
            issuer: $"https://vstoken.dev.azure.com/{Guid.NewGuid()}",   // placeholder; we read the real issuer from ADO below
            subject: wifSubject,
            audiences: new[] { "api://AzureADTokenExchange" },
            ct).ConfigureAwait(false);

        return new WifAzureRmResult(
            AppId: appId,
            AppObjectId: appObjectId,
            ServicePrincipalObjectId: spObjectId,
            ServiceConnectionId: sc.Id,
            FederatedCredentialId: fcId,
            WifSubject: wifSubject);
    }

    private static void Validate(WifAzureRmRequest r)
    {
        if (string.IsNullOrEmpty(r.AdoOrgUrl)) throw new ArgumentException("AdoOrgUrl is required.", nameof(r));
        if (r.AdoPat is null) throw new ArgumentException("AdoPat is required.", nameof(r));
        if (string.IsNullOrEmpty(r.Project)) throw new ArgumentException("Project is required.", nameof(r));
        if (string.IsNullOrEmpty(r.Name)) throw new ArgumentException("Name is required.", nameof(r));
        if (string.IsNullOrEmpty(r.SubscriptionId)) throw new ArgumentException("SubscriptionId is required.", nameof(r));
        if (string.IsNullOrEmpty(r.SubscriptionName)) throw new ArgumentException("SubscriptionName is required.", nameof(r));
        if (string.IsNullOrEmpty(r.TenantId)) throw new ArgumentException("TenantId is required.", nameof(r));
        if (string.IsNullOrEmpty(r.ResourceGroup)) throw new ArgumentException("ResourceGroup is required.", nameof(r));
    }

    private static async Task<(string AppId, string ObjectId)> CreateAdAppAsync(Tool az, string displayName, CancellationToken ct)
    {
        var plan = Az.Raw(az, "ad", "app", "create", "--display-name", displayName, "--output", "json");
        var json = await CaptureJsonAsync(plan, $"az ad app create --display-name {displayName}", ct).ConfigureAwait(false);
        var appId = json.GetProperty("appId").GetString()
            ?? throw new InvalidOperationException("az ad app create: response missing 'appId'.");
        var objectId = (json.TryGetProperty("id", out var idElem) ? idElem.GetString() : null)
            ?? (json.TryGetProperty("objectId", out var oidElem) ? oidElem.GetString() : null)
            ?? throw new InvalidOperationException("az ad app create: response missing 'id'/'objectId'.");
        return (appId, objectId);
    }

    private static async Task<string> CreateAdSpAsync(Tool az, string appId, CancellationToken ct)
    {
        var plan = Az.Raw(az, "ad", "sp", "create", "--id", appId, "--output", "json");
        var json = await CaptureJsonAsync(plan, $"az ad sp create --id {appId}", ct).ConfigureAwait(false);
        return (json.TryGetProperty("id", out var idElem) ? idElem.GetString() : null)
            ?? (json.TryGetProperty("objectId", out var oidElem) ? oidElem.GetString() : null)
            ?? throw new InvalidOperationException("az ad sp create: response missing 'id'/'objectId'.");
    }

    private static async Task CreateRoleAssignmentAsync(Tool az, string assigneeObjectId, string roleName, string scope, CancellationToken ct)
    {
        var plan = Az.Raw(az,
            "role", "assignment", "create",
            "--assignee-object-id", assigneeObjectId,
            "--assignee-principal-type", "ServicePrincipal",
            "--role", roleName,
            "--scope", scope,
            "--output", "none");
        await DispatchAsync(plan, $"az role assignment create --role {roleName} --scope {scope}", ct).ConfigureAwait(false);
    }

    private static async Task<string> CreateFederatedCredentialAsync(
        Tool az,
        string appObjectId,
        string fcName,
        string issuer,
        string subject,
        string[] audiences,
        CancellationToken ct)
    {
        // az ad app federated-credential create takes a JSON blob via
        // --parameters. We construct the JSON here and pass it inline.
        var fcPayload = new
        {
            name = fcName,
            issuer,
            subject,
            audiences,
            description = "Created by Tamp.AdoServiceConnection.V1",
        };
        var json = JsonSerializer.Serialize(fcPayload);

        var plan = Az.Raw(az,
            "ad", "app", "federated-credential", "create",
            "--id", appObjectId,
            "--parameters", json,
            "--output", "json");
        var response = await CaptureJsonAsync(plan, "az ad app federated-credential create", ct).ConfigureAwait(false);
        return response.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("az ad app federated-credential create: response missing 'id'.");
    }

    private static string? ExtractWifSubject(ServiceEndpoint sc)
    {
        // ADO returns the wifSubject as a key under authorization.parameters.
        // The exact key name has shifted between API versions:
        //   - 7.1-preview returns 'workloadIdentityFederationSubject'
        //   - earlier previews used 'workloadIdentityFederationIssuerSubject'
        if (sc.Authorization?.Parameters is null) return null;
        return sc.Authorization.Parameters.TryGetValue("workloadIdentityFederationSubject", out var v1) ? v1
             : sc.Authorization.Parameters.TryGetValue("workloadIdentityFederationIssuerSubject", out var v2) ? v2
             : null;
    }

    private static async Task<JsonElement> CaptureJsonAsync(CommandPlan plan, string description, CancellationToken ct)
    {
        var result = await Task.Run(() => ProcessRunner.Capture(plan), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{description} failed with exit code {result.ExitCode}. Stderr: {Truncate(result.StderrText)}");
        try
        {
            using var doc = JsonDocument.Parse(result.StdoutText);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{description}: stdout was not valid JSON: {Truncate(result.StdoutText)}", ex);
        }
    }

    private static async Task DispatchAsync(CommandPlan plan, string description, CancellationToken ct)
    {
        var result = await Task.Run(() => ProcessRunner.Capture(plan), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{description} failed with exit code {result.ExitCode}. Stderr: {Truncate(result.StderrText)}");
    }

    private static string Truncate(string s) => s.Length <= 512 ? s : s[..512] + "…";
}
