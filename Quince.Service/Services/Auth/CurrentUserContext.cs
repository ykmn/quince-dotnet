namespace Quince.Service.Services.Auth;

/// <summary>Holder for the logged-in user, flowed into the component tree as a cascading value from
/// <see cref="Pages.App"/> (itself fed by root-component parameters set in _Host.cshtml from
/// <c>HttpContext.Items</c>, populated by the auth middleware). A plain cascading value rather than a
/// DI-registered scoped service deliberately — Blazor Server's interactive circuit gets a *new* DI
/// scope once the SignalR connection takes over from the initial HTTP prerender, so a scoped service
/// set during prerender would silently reset; component parameters/cascading values are the part of
/// the render tree that's guaranteed to carry over that transition.</summary>
public class CurrentUserContext
{
    public bool AuthRequired { get; set; }
    public string? Username { get; set; }
    public bool IsAdmin { get; set; }
}
