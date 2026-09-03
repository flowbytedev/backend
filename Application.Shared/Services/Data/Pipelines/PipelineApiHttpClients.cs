using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// The HTTP clients the API source and destination run on, defined once for every host that executes a
/// pipeline.
/// <para>
/// This used to be written out in each host's <c>Program.cs</c>, and the duplication is what made the
/// second client a hazard rather than a feature. A pipeline can run in the web app or in the scheduler, so
/// the two registrations have to agree; when they did not — one host redeployed, the other not yet — the
/// symptom was a credential that skipped certificate validation in one process and enforced it in the
/// other, reported as an ordinary certificate error. One call, in one place, removes that failure mode.
/// </para>
/// </summary>
public static class PipelineApiHttpClients
{
    /// <summary>
    /// Registers both clients. Every host that resolves <see cref="IPipelineApiClient"/> must call this.
    /// </summary>
    public static IServiceCollection AddPipelineApiHttpClients(this IServiceCollection services)
    {
        // Automatic redirects DISABLED on purpose: .NET drops the Authorization header when it follows a
        // redirect to another origin, but it keeps custom headers, so an X-Api-Key credential would be
        // handed to whatever host a remote server named in a Location response. PipelineApiClient follows
        // same-origin redirects itself and refuses the rest.
        services.AddHttpClient(PipelineApiClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });

        // The same client for credentials that set "Accept an invalid TLS certificate" — an internal
        // endpoint whose certificate this server does not trust. A separate handler rather than a callback
        // on the one above, because the factory pools one handler per name: mutating that handler would stop
        // validating certificates for every credential in the process, and building one per request would
        // leak sockets. Only a credential with the flag set reaches this client — see
        // PipelineApiClient.ClientNameFor.
        services.AddHttpClient(PipelineApiClient.InsecureHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });

        return services;
    }
}
