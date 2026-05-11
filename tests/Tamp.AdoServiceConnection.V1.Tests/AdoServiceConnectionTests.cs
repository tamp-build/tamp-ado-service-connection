using System.IO;
using Tamp;
using Xunit;

namespace Tamp.AdoServiceConnection.V1.Tests;

/// <summary>
/// Coverage for the orchestrator's argument-validation surface. The
/// 6-step flow itself spans az + ADO REST + JSON parsing — that
/// integration belongs in a consumer pipeline against a real ADO org
/// + Azure tenant, not in unit tests that mock both layers.
///
/// What IS unit-testable here:
///   - WifAzureRmRequest field equality + record semantics
///   - Argument validation (every required field rejected when missing)
///   - Null guards on the public entry point
///   - Defaults applied correctly (AppDisplayName fallback, fc name)
/// </summary>
public sealed class AdoServiceConnectionTests
{
    private static Tool FakeTool() =>
        new(AbsolutePath.Create(Path.Combine(Path.GetTempPath(), "az")));

    private static WifAzureRmRequest ValidRequest(Action<WifAzureRmRequestBuilder>? customize = null)
    {
        var b = new WifAzureRmRequestBuilder
        {
            AdoOrgUrl = "https://dev.azure.com/i3solutions/",
            AdoPat = new Secret("ADO PAT", "fake-pat"),
            Project = "Strata",
            Name = "sp-strata-cicd-test",
            SubscriptionId = "00000000-0000-0000-0000-000000000001",
            SubscriptionName = "Strata Test",
            TenantId = "11111111-1111-1111-1111-111111111111",
            ResourceGroup = "rg-strata-test",
        };
        customize?.Invoke(b);
        return b.Build();
    }

    [Fact]
    public async Task Null_Az_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            AdoServiceConnection.CreateWifAzureRmAsync(null!, ValidRequest()));
    }

    [Fact]
    public async Task Null_Request_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            AdoServiceConnection.CreateWifAzureRmAsync(FakeTool(), null!));
    }

    [Theory]
    [InlineData("AdoOrgUrl")]
    [InlineData("Project")]
    [InlineData("Name")]
    [InlineData("SubscriptionId")]
    [InlineData("SubscriptionName")]
    [InlineData("TenantId")]
    [InlineData("ResourceGroup")]
    public async Task Missing_Required_String_Field_Throws_ArgumentException(string field)
    {
        var request = ValidRequest(b =>
        {
            switch (field)
            {
                case "AdoOrgUrl": b.AdoOrgUrl = ""; break;
                case "Project": b.Project = ""; break;
                case "Name": b.Name = ""; break;
                case "SubscriptionId": b.SubscriptionId = ""; break;
                case "SubscriptionName": b.SubscriptionName = ""; break;
                case "TenantId": b.TenantId = ""; break;
                case "ResourceGroup": b.ResourceGroup = ""; break;
            }
        });
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            AdoServiceConnection.CreateWifAzureRmAsync(FakeTool(), request));
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public async Task Missing_AdoPat_Throws()
    {
        var request = ValidRequest(b => b.AdoPat = null);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AdoServiceConnection.CreateWifAzureRmAsync(FakeTool(), request));
    }

    // ---- record semantics ----

    [Fact]
    public void WifAzureRmRequest_Defaults_RoleDefinitionName_To_Contributor()
    {
        var pat = new Secret("p", "v");
        var r = new WifAzureRmRequest(
            AdoOrgUrl: "x",
            AdoPat: pat,
            Project: "p",
            Name: "n",
            SubscriptionId: "s",
            SubscriptionName: "sn",
            TenantId: "t",
            ResourceGroup: "rg");
        Assert.Equal("Contributor", r.RoleDefinitionName);
        Assert.Null(r.AppDisplayName);
        Assert.Null(r.FederatedCredentialName);
    }

    [Fact]
    public void WifAzureRmRequest_Custom_Role_Round_Trips()
    {
        var pat = new Secret("p", "v");
        var r = new WifAzureRmRequest(
            AdoOrgUrl: "x", AdoPat: pat, Project: "p", Name: "n",
            SubscriptionId: "s", SubscriptionName: "sn", TenantId: "t",
            ResourceGroup: "rg",
            RoleDefinitionName: "User Access Administrator");
        Assert.Equal("User Access Administrator", r.RoleDefinitionName);
    }

    [Fact]
    public void WifAzureRmResult_Carries_All_Six_Ids()
    {
        var r = new WifAzureRmResult(
            AppId: "app-id",
            AppObjectId: "app-obj",
            ServicePrincipalObjectId: "sp-obj",
            ServiceConnectionId: "sc-id",
            FederatedCredentialId: "fc-id",
            WifSubject: "sc/abc/def");
        Assert.Equal("app-id", r.AppId);
        Assert.Equal("app-obj", r.AppObjectId);
        Assert.Equal("sp-obj", r.ServicePrincipalObjectId);
        Assert.Equal("sc-id", r.ServiceConnectionId);
        Assert.Equal("fc-id", r.FederatedCredentialId);
        Assert.Equal("sc/abc/def", r.WifSubject);
    }

    [Fact]
    public void WifAzureRmRequest_Records_Implement_Equality()
    {
        var pat = new Secret("p", "v");
        var a = new WifAzureRmRequest(
            AdoOrgUrl: "x", AdoPat: pat, Project: "p", Name: "n",
            SubscriptionId: "s", SubscriptionName: "sn", TenantId: "t",
            ResourceGroup: "rg");
        var b = new WifAzureRmRequest(
            AdoOrgUrl: "x", AdoPat: pat, Project: "p", Name: "n",
            SubscriptionId: "s", SubscriptionName: "sn", TenantId: "t",
            ResourceGroup: "rg");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}

/// <summary>Mutable builder for the otherwise-immutable WifAzureRmRequest record — keeps the test setup clean.</summary>
internal sealed class WifAzureRmRequestBuilder
{
    public string AdoOrgUrl { get; set; } = "";
    public Secret? AdoPat { get; set; }
    public string Project { get; set; } = "";
    public string Name { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string RoleDefinitionName { get; set; } = "Contributor";
    public string? AppDisplayName { get; set; }
    public string? FederatedCredentialName { get; set; }

    public WifAzureRmRequest Build() => new(
        AdoOrgUrl,
        AdoPat!,
        Project,
        Name,
        SubscriptionId,
        SubscriptionName,
        TenantId,
        ResourceGroup,
        RoleDefinitionName,
        AppDisplayName,
        FederatedCredentialName);
}
