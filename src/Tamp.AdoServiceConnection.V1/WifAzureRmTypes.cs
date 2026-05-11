namespace Tamp.AdoServiceConnection.V1;

/// <summary>
/// Inputs for end-to-end Workload Identity Federation service
/// connection creation. Everything required for the 6-step orchestration.
/// </summary>
/// <param name="AdoOrgUrl">ADO organization URL, e.g. <c>https://dev.azure.com/i3solutions/</c>. Trailing slash optional.</param>
/// <param name="AdoPat">Personal access token with <c>Service Connections (Read &amp; Manage)</c> scope. Typed as Secret so it joins the redaction table.</param>
/// <param name="Project">ADO project name or id the SC will be scoped to.</param>
/// <param name="Name">Display name for the new SC. Reused as the Entra app display name unless <see cref="AppDisplayName"/> is set.</param>
/// <param name="SubscriptionId">Azure subscription GUID.</param>
/// <param name="SubscriptionName">Subscription display name. Required by ADO's create-SC payload.</param>
/// <param name="TenantId">Entra tenant GUID.</param>
/// <param name="ResourceGroup">Resource group the role assignment scopes to.</param>
/// <param name="RoleDefinitionName">Built-in or custom role name. Default: <c>Contributor</c>.</param>
/// <param name="AppDisplayName">Override for the Entra app's display name. Defaults to <see cref="Name"/>.</param>
/// <param name="FederatedCredentialName">Name for the Entra federated credential. Default: <c>{Name}-fc</c>.</param>
public sealed record WifAzureRmRequest(
    string AdoOrgUrl,
    Secret AdoPat,
    string Project,
    string Name,
    string SubscriptionId,
    string SubscriptionName,
    string TenantId,
    string ResourceGroup,
    string RoleDefinitionName = "Contributor",
    string? AppDisplayName = null,
    string? FederatedCredentialName = null);

/// <summary>
/// Outputs from the 6-step orchestration. Capture this and persist it
/// — the federated-credential subject is what's needed to add more
/// trusted scopes later, and the app/SP IDs are what every <c>az role
/// assignment</c> against this identity needs.
/// </summary>
public sealed record WifAzureRmResult(
    string AppId,
    string AppObjectId,
    string ServicePrincipalObjectId,
    string ServiceConnectionId,
    string FederatedCredentialId,
    string WifSubject);
