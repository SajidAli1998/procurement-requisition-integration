using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Procurement.AssistantApi.Model;
using Procurement.SyntheticData;

namespace Procurement.AssistantApi.Infrastructure;

// Local and test use only. In production the identity provider issues tokens to the assistant
// on behalf of the signed-in user.
public static class DevTokens
{
    public static SymmetricSecurityKey Key(AuthOptions auth) => new(Encoding.UTF8.GetBytes(auth.SigningKey));

    // Someone not in the directory, such as a developer trying the API, gets read and draft.
    public static string[] ScopesFor(Employee? employee) => employee is null
        ? [Scopes.Read, Scopes.Draft]
        : [Scopes.Read, .. employee.CanRaise ? [Scopes.Draft] : Array.Empty<string>(), .. employee.ProcurementDelegate ? [Scopes.ReadAll] : Array.Empty<string>()];

    public static string Issue(AuthOptions auth, string user, IEnumerable<string> scopes, TimeSpan? lifetime = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = auth.Issuer,
            Audience = auth.Audience,
            Subject = new ClaimsIdentity([new Claim("sub", user), new Claim("scope", string.Join(' ', scopes)), new Claim("azp", "procurement-assistant")]),
            NotBefore = now,
            IssuedAt = now,
            Expires = now + (lifetime ?? TimeSpan.FromHours(1)),
            SigningCredentials = new SigningCredentials(Key(auth), SecurityAlgorithms.HmacSha256),
        });
    }
}
