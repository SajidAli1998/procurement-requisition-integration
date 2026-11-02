using System.Net;
using Polly;
using Polly.Timeout;
using Procurement.Sync.Http;

namespace Procurement.Sync.UnitTests;

public sealed class RetryClassifierTests
{
    private static HttpRequestMessage Request(HttpMethod method, bool ifMatch = false)
    {
        var request = new HttpRequestMessage(method, "http://sap/x");
        if (ifMatch) request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        return request;
    }

    private static Outcome<HttpResponseMessage> Status(HttpStatusCode code) => Outcome.FromResult(new HttpResponseMessage(code));

    [Theory]
    [InlineData("POST", HttpStatusCode.TooManyRequests, true)]
    [InlineData("POST", HttpStatusCode.ServiceUnavailable, true)]
    [InlineData("POST", HttpStatusCode.InternalServerError, false)]
    [InlineData("POST", HttpStatusCode.GatewayTimeout, false)]
    [InlineData("GET", HttpStatusCode.InternalServerError, true)]
    [InlineData("GET", HttpStatusCode.BadGateway, true)]
    [InlineData("GET", HttpStatusCode.BadRequest, false)]
    [InlineData("POST", HttpStatusCode.UnprocessableEntity, false)]
    [InlineData("PUT", HttpStatusCode.PreconditionFailed, false)]
    public void Status_codes(string method, HttpStatusCode status, bool retry) =>
        RetryClassifier.ShouldRetry(Status(status), Request(new HttpMethod(method))).ShouldBe(retry);

    [Fact]
    public void A_post_with_no_response_is_never_retried_blindly() =>
        RetryClassifier.ShouldRetry(Outcome.FromException<HttpResponseMessage>(new HttpRequestException("reset")), Request(HttpMethod.Post))
            .ShouldBeFalse();

    [Fact]
    public void A_get_that_timed_out_is_retried() =>
        RetryClassifier.ShouldRetry(Outcome.FromException<HttpResponseMessage>(new TimeoutRejectedException()), Request(HttpMethod.Get))
            .ShouldBeTrue();

    [Fact]
    public void A_conditional_patch_is_safe_to_repeat_but_an_unconditional_one_is_not()
    {
        var failure = Outcome.FromException<HttpResponseMessage>(new HttpRequestException("reset"));
        RetryClassifier.ShouldRetry(failure, Request(HttpMethod.Patch, ifMatch: true)).ShouldBeTrue();
        RetryClassifier.ShouldRetry(failure, Request(HttpMethod.Patch)).ShouldBeFalse();
    }
}
