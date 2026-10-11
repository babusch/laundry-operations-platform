using System.Collections.Immutable;
using System.Security.Claims;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Authentication;

namespace Laundry.Api.Tests;

public sealed class ManagementMembershipTests
{
    private const string Issuer = "https://identity.example.test/realms/demo";

    [Fact]
    public void VerifiedManagementTicket_ProducesHumanWithStableIdentity()
    {
        Assert.True(ManagementSessionAccess.TryReadActor(Ticket(), Issuer, out var actor));
        Assert.Equal(new ManagementActor(Issuer, "synthetic-person", ManagementActorKind.Human, true), actor);
    }

    [Theory]
    [InlineData("gateway_scheme")]
    [InlineData("gateway_identity")]
    [InlineData("wrong_issuer")]
    [InlineData("no_marker")]
    [InlineData("no_subject")]
    [InlineData("duplicate_subject")]
    [InlineData("multiple_identities")]
    public void ActorResolution_DoesNotTrustOtherSchemesOrAmbiguousClaims(string invalid)
    {
        Assert.False(ManagementSessionAccess.TryReadActor(Ticket(invalid), Issuer, out var actor));
        Assert.Null(actor);
    }

    [Fact]
    public void AnonymousOrFailedTicket_CannotBecomeHuman()
    {
        Assert.False(ManagementSessionAccess.TryReadActor(AuthenticateResult.NoResult(), Issuer, out _));
        Assert.False(ManagementSessionAccess.TryReadActor(AuthenticateResult.Fail("synthetic failure"), Issuer, out _));
    }

    private static AuthenticateResult Ticket(string invalid = "")
    {
        var claims = new List<Claim>
        {
            new("iss", invalid == "wrong_issuer" ? "https://other.example.test" : Issuer)
        };
        if (invalid != "no_subject") claims.Add(new Claim("sub", "synthetic-person"));
        if (invalid == "duplicate_subject") claims.Add(new Claim("sub", "another-person"));
        if (invalid != "no_marker") claims.Add(new Claim(ManagementAuthentication.SessionMarker, "interactive"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims,
            invalid == "gateway_identity" ? "Bearer" : ManagementAuthentication.CookieScheme));
        if (invalid == "multiple_identities") principal.AddIdentity(new ClaimsIdentity([new Claim("sub", "other")], "Bearer"));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, new AuthenticationProperties(),
            invalid == "gateway_scheme" ? "Bearer" : ManagementAuthentication.CookieScheme));
    }
}
