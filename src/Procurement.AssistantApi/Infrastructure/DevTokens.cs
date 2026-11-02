using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Procurement.AssistantApi.Model;

namespace Procurement.AssistantApi.Infrastructure;

/// <summary>
/// Issues signed development tokens so the API can be exercised locally and in tests.
/// In production tokens come from the organisation's identity provider, issued to the assistant
/// on behalf of the signed-in user (OAuth on-behalf-of), and this class is not used.
/// </summary>
public static class DevTokens
{
    public static SymmetricSecurityKey Key(AuthOptions auth) => new(Encoding.UTF8.GetBytes(auth.SigningKey));

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
