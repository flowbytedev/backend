using System.Diagnostics;
using System.Security.Claims;
using Application.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Web;

namespace Application.Auth;

/// <summary>
/// Serialises the signed-in user into the rendered page so the WebAssembly client can pick it up
/// without a round trip.
/// </summary>
/// <remarks>
/// This replaces the version the ASP.NET Identity scaffolding put under <c>Components/Account</c>.
/// That one resolved the user through <c>UserManager&lt;ApplicationUser&gt;</c>; there is no
/// <c>UserManager</c> any more, so this reads the claims the OIDC handler already produced.
/// <para>
/// Its counterpart is <see cref="PersistentAuthenticationStateProvider"/> in the client project,
/// which deserialises <see cref="UserInfo"/> on the other side. The two must agree on the key —
/// <c>nameof(UserInfo)</c> — and on which claims are carried.
/// </para>
/// <para>
/// Only display information crosses: an id, an email and roles. No tokens. The client authenticates
/// subsequent requests with the auth cookie, which the browser attaches on its own.
/// </para>
/// </remarks>
public sealed class PersistingServerAuthenticationStateProvider : ServerAuthenticationStateProvider, IDisposable
{
    private readonly PersistentComponentState _state;
    private readonly PersistingComponentStateSubscription _subscription;

    private Task<AuthenticationState>? _authenticationStateTask;

    public PersistingServerAuthenticationStateProvider(PersistentComponentState state)
    {
        _state = state;
        _subscription = state.RegisterOnPersisting(OnPersistingAsync, RenderMode.InteractiveWebAssembly);
        AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> task) => _authenticationStateTask = task;

    private async Task OnPersistingAsync()
    {
        if (_authenticationStateTask is null)
        {
            throw new UnreachableException(
                $"{nameof(AuthenticationStateChanged)} must fire before the state is persisted.");
        }

        var authenticationState = await _authenticationStateTask;
        var principal = authenticationState.User;

        if (principal.Identity?.IsAuthenticated != true)
        {
            return;
        }

        // NameIdentifier is identity's `sub`, put there by IdentityClaims.Build — one id for a
        // person across every Flowbyte application. The Entra object id is never consulted.
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst(ClaimTypes.Email)?.Value
                    ?? principal.FindFirst("preferred_username")?.Value
                    ?? principal.Identity.Name;

        if (userId is null || email is null)
        {
            return;
        }

        _state.PersistAsJson(nameof(UserInfo), new UserInfo
        {
            UserId = userId,
            Email = email,
            Roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
        });
    }

    public void Dispose()
    {
        _subscription.Dispose();
        AuthenticationStateChanged -= OnAuthenticationStateChanged;
    }
}
