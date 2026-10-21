using Microsoft.Extensions.Configuration;
using CarnivalApp.Api.Identity;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Auth provider selection (ADR-0007): Entra ID, on-prem ADFS/generic-OIDC, or
/// the dev-auth fallback — chosen purely by configuration (§7.3), with an
/// explicit override winning over section inference.
/// </summary>
public sealed class IdentityProviderResolutionTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Entra_is_chosen_when_AzureAd_has_tenant_and_client()
    {
        var config = Config(("AzureAd:TenantId", "t"), ("AzureAd:ClientId", "c"));
        Assert.Equal(IdentitySetup.IdentityProvider.Entra, IdentitySetup.ResolveProvider(config));
    }

    [Fact]
    public void Adfs_is_chosen_when_only_the_Adfs_section_is_populated()
    {
        var config = Config(("Adfs:Authority", "https://adfs.school/adfs"), ("Adfs:ClientId", "rp"));
        Assert.Equal(IdentitySetup.IdentityProvider.Adfs, IdentitySetup.ResolveProvider(config));
    }

    [Fact]
    public void No_provider_configured_falls_back_to_dev_auth()
    {
        Assert.Equal(IdentitySetup.IdentityProvider.DevAuth, IdentitySetup.ResolveProvider(Config()));
    }

    [Fact]
    public void Explicit_provider_wins_over_section_inference()
    {
        // AzureAd is populated, but the school pins ADFS explicitly.
        var config = Config(
            ("Identity:Provider", "Adfs"),
            ("AzureAd:TenantId", "t"), ("AzureAd:ClientId", "c"),
            ("Adfs:Authority", "https://adfs.school/adfs"), ("Adfs:ClientId", "rp"));
        Assert.Equal(IdentitySetup.IdentityProvider.Adfs, IdentitySetup.ResolveProvider(config));
    }

    [Fact]
    public void An_unrecognised_explicit_provider_falls_through_to_inference()
    {
        var config = Config(("Identity:Provider", "Banana"),
            ("AzureAd:TenantId", "t"), ("AzureAd:ClientId", "c"));
        Assert.Equal(IdentitySetup.IdentityProvider.Entra, IdentitySetup.ResolveProvider(config));
    }

    [Fact]
    public void A_half_configured_section_does_not_select_that_provider()
    {
        // Authority without ClientId is incomplete → not ADFS → dev-auth.
        var config = Config(("Adfs:Authority", "https://adfs.school/adfs"));
        Assert.Equal(IdentitySetup.IdentityProvider.DevAuth, IdentitySetup.ResolveProvider(config));
    }
}
