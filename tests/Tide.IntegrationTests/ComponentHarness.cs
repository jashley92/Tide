using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tide.Application.Security;

namespace Tide.IntegrationTests;

/// <summary>
/// A component rendered in the real app's services, signed in as someone, and handed over so a test can do what its
/// buttons would and read the markup after.
/// </summary>
public static class ComponentHarness
{
    /// <summary>Who's signed in: the user's id, name and roles.</summary>
    public sealed record SignIn(string UserId, string Name, params string[] Roles)
    {
        /// <summary>A tester with the dev sign-in's default roles (Analyst and Admin).</summary>
        public static readonly SignIn Analyst = new("dev:tester", "Tester", "Analyst", "Admin");
    }

    private sealed class Host<T> : ComponentBase where T : IComponent
    {
        [Parameter] public IReadOnlyDictionary<string, object?> Values { get; set; } = new Dictionary<string, object?>();
        [Parameter] public Action<T> Got { get; set; } = _ => { };

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<T>(0);
            foreach (var (name, value) in Values) builder.AddComponentParameter(1, name, value);
            builder.AddComponentReferenceCapture(2, c => Got((T)c));
            builder.CloseComponent();
        }
    }

    public sealed record Rendered<T>(HtmlRenderer Renderer, HtmlRootComponent Root, T Component, AsyncServiceScope Scope, HttpContext Signedin) : IAsyncDisposable
    {
        public Task<string> Html() => Renderer.Dispatcher.InvokeAsync(Root.ToHtmlString);

        /// <summary>The markup once it shows <paramref name="text"/> (a dialog opened after the page read the database).</summary>
        public async Task<string> HtmlOnce(string text)
        {
            // Up to 15 seconds: a page's live feed waits LiveChanges.Pause before it re-reads, and a loaded machine can
            // take several seconds more. Only a failure waits that long.
            for (var i = 0; i < 150; i++)
            {
                var html = await Html();
                if (html.Contains(text, StringComparison.Ordinal)) return html;
                await Task.Delay(100);
            }
            return await Html();
        }

        /// <summary>Waits until <paramref name="done"/> holds, read on the renderer's thread, so it never sees a reload
        /// halfway (a page that has set one field and not yet the next); gives up after <paramref name="seconds"/>, for
        /// the assertion after to say what's wrong.</summary>
        public async Task<bool> Until(Func<T, bool> done, int seconds = 15)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                if (await Renderer.Dispatcher.InvokeAsync(() => done(Component))) return true;
                if (DateTime.UtcNow > until) return false;
                await Task.Delay(50);
            }
        }

        /// <summary>Does what a button would, on the renderer's thread, without waiting for the dialog it opens.</summary>
        public Task Press(Func<T, Task> what) => Renderer.Dispatcher.InvokeAsync(() => { _ = Pressed(what); });

        /// <summary>Does what a button would, on the renderer's thread, and waits for it.</summary>
        public Task Click(Func<T, Task> what) => Renderer.Dispatcher.InvokeAsync(() => Pressed(what));

        /// <summary>The same, handing back what the button started.</summary>
        public Task Pressed(Func<T, Task> what)
        {
            // As a request would: the sign-in is the one the page rendered with (a test's own flow doesn't carry it).
            Scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = Signedin;
            return what(Component);
        }

        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            await Scope.DisposeAsync();
        }
    }

    /// <summary>A scope of the app's services signed in as <paramref name="who"/>, for a test to set things up as them.</summary>
    public static AsyncServiceScope SignedIn(TideApp app, SignIn who)
    {
        var scope = app.Services.CreateAsyncScope();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, who.UserId), new(ClaimTypes.Name, who.Name), new(ClaimTypes.Email, "tester@example.com") };
        claims.AddRange(who.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(RoleDefinitions.PermissionsForRoleNames(who.Roles).Select(p => new Claim(AppClaimTypes.Permission, p.ToString())));
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return scope;
    }

    /// <summary>Renders <typeparamref name="T"/> at <paramref name="path"/>, signed in as <paramref name="as"/> (an analyst by default),
    /// with the app at <paramref name="baseUri"/> (<c>http://localhost/tide/</c> for a sub-path).</summary>
    public static async Task<Rendered<T>> RenderAsync<T>(TideApp app, string path, Dictionary<string, object?>? values = null, SignIn? @as = null,
        string baseUri = "http://localhost/")
        where T : IComponent
    {
        var scope = SignedIn(app, @as ?? SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var signedIn = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!;
        if (sp.GetRequiredService<NavigationManager>() is IHostEnvironmentNavigationManager nav) nav.Initialize(baseUri, baseUri.TrimEnd('/') + path);

        var renderer = new HtmlRenderer(sp, sp.GetRequiredService<ILoggerFactory>());
        T? got = default;
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var r = await renderer.RenderComponentAsync<Host<T>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Values"] = (IReadOnlyDictionary<string, object?>)(values ?? []),
                ["Got"] = (Action<T>)(c => got = c),
            }));
            await r.QuiescenceTask;
            return r;
        });
        return new Rendered<T>(renderer, root, got!, scope, signedIn);
    }
}
