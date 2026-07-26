using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class ImgurUploadServiceTests
{
    private static readonly byte[] SmallPng = Encoding.ASCII.GetBytes("fake-png-bytes");

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();
        public string? CapturedAuthorization { get; private set; }
        public string? CapturedContentType { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            CapturedAuthorization = request.Headers.Authorization?.ToString();
            CapturedContentType = request.Content?.Headers.ContentType?.MediaType;
            return Task.FromResult(_responder(request));
        }
    }

    private static ImgurUploadService CreateService(RecordingHandler handler, string clientId = "test-id")
        => new(() => clientId, new HttpClient(handler));

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task UploadPng_Success_ParsesLinkAndDeleteHash_AndSendsExpectedRequest()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{"success":true,"status":200,"data":{"link":"https://i.imgur.com/abc123.png","deletehash":"del-42"}}"""));
        var service = CreateService(handler);

        var result = await service.UploadPngAsync(SmallPng);

        Assert.True(result.Success);
        Assert.Equal("https://i.imgur.com/abc123.png", result.Link);
        Assert.Equal("del-42", result.DeleteHash);
        Assert.False(result.NotConfigured);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.imgur.com/3/image", request.RequestUri?.ToString());
        Assert.Equal("Client-ID test-id", handler.CapturedAuthorization);
        Assert.Equal("multipart/form-data", handler.CapturedContentType);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task UploadPng_HttpError_ReturnsFailureWithoutThrowing(HttpStatusCode status)
    {
        var handler = new RecordingHandler(_ => Json(status, """{"success":false}"""));
        var service = CreateService(handler);

        var result = await service.UploadPngAsync(SmallPng);

        Assert.False(result.Success);
        Assert.False(result.NotConfigured);
        Assert.Contains(((int)status).ToString(), result.Error);
    }

    [Fact]
    public async Task UploadPng_MalformedJson_ReturnsGracefulFailure()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "not-json{{"));
        var service = CreateService(handler);

        var result = await service.UploadPngAsync(SmallPng);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task UploadPng_SuccessWithoutLink_ReturnsFailure()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{"success":true,"status":200,"data":{}}"""));
        var service = CreateService(handler);

        var result = await service.UploadPngAsync(SmallPng);

        Assert.False(result.Success);
        Assert.Null(result.Link);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UploadPng_BlankClientId_ShortCircuitsWithoutRequest(string clientId)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));
        var service = CreateService(handler, clientId);

        var result = await service.UploadPngAsync(SmallPng);

        Assert.False(result.Success);
        Assert.True(result.NotConfigured);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UploadPng_OversizedPayload_FailsFastWithoutRequest()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));
        var service = CreateService(handler);

        var result = await service.UploadPngAsync(new byte[21 * 1024 * 1024]);

        Assert.False(result.Success);
        Assert.False(result.NotConfigured);
        Assert.Empty(handler.Requests);
    }
}
