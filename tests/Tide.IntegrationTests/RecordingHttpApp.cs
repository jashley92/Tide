using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Tide.IntegrationTests;

/// <summary>The app with every outgoing HTTP call recorded and answered locally, so a test can hold that none is made.</summary>
public sealed class RecordingHttpApp : TideApp
{
    private int _calls;

    /// <summary>How many outgoing HTTP calls the app has made.</summary>
    public int Calls => Volatile.Read(ref _calls);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new Recording(this))));
    }

    private sealed class Recording(RecordingHttpApp app) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref app._calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
