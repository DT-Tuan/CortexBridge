using CortexBridge.Api.Auth;

namespace CortexBridge.Api.Tests;

public class TokenIssuerTests
{
    [Fact]
    public void Hash_IsDeterministic()
    {
        Assert.Equal(TokenIssuer.Hash("cb_abc"), TokenIssuer.Hash("cb_abc"));
    }

    [Fact]
    public void Hash_ChangesOnDifferentInput()
    {
        Assert.NotEqual(TokenIssuer.Hash("cb_abc"), TokenIssuer.Hash("cb_abd"));
    }

    [Fact]
    public void Hash_LooksLikeHexSha256()
    {
        var h = TokenIssuer.Hash("cb_test");
        Assert.Equal(64, h.Length);
        Assert.Matches("^[0-9a-f]+$", h);
    }
}
