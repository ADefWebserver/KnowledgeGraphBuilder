using System.Net;
using KnowledgeGraphBuilder.Shared.Knowledge;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KnowledgeGraphBuilder.Shared.Markdown;

/// <summary>
/// Renders Markdown to HTML for the preview and for chat. Raw HTML is never rendered, images become text,
/// and in chat external links are inert (D17).
/// </summary>
public static class SafeMarkdown
{
    public static string PageHref(string path) => "./?p=" + Uri.EscapeDataString(path);

    public static string CreateHref(string target, string? fromPath = null) =>
        "./?create=" + Uri.EscapeDataString(target) + (fromPath is null ? "" : "&p=" + Uri.EscapeDataString(fromPath));

    public static string ForPreview(string markdown, Func<string, string?> resolve, Func<string, string?>? resolveRelative = null, string? currentPath = null) =>
        Render(markdown, new Options(resolve, resolveRelative, Chat: false, currentPath));

    public static string ForChat(string markdown, Func<string, string?> resolve) =>
        Render(markdown, new Options(resolve, null, Chat: true, null));

    sealed record Options(Func<string, string?> Resolve, Func<string, string?>? ResolveRelative, bool Chat, string? CurrentPath);

    static string Render(string markdown, Options options)
    {
        var (_, body) = FrontMatter.Split(markdown ?? "");
        var doc = Markdig.Markdown.Parse(body, MarkdownPipelines.Render);
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        MarkdownPipelines.Render.Setup(renderer);

        renderer.ObjectRenderers.RemoveAll(r => r is LinkInlineRenderer or AutolinkInlineRenderer);
        renderer.ObjectRenderers.Insert(0, new WikiRenderer(options));
        renderer.ObjectRenderers.Insert(0, new SafeLinkRenderer(options));
        renderer.ObjectRenderers.Insert(0, new SafeAutolinkRenderer(options));

        renderer.Render(doc);
        writer.Flush();
        return writer.ToString();
    }

    static string E(string s) => WebUtility.HtmlEncode(s);

    static void WriteExternal(HtmlRenderer r, string text, string url, Options o)
    {
        if (o.Chat)
        {
            // Inert: no href, URL visible.
            r.Write("<span class=\"ext\" title=\"External link (not clickable)\">")
             .Write(E(text));
            if (!string.Equals(text, url, StringComparison.Ordinal)) r.Write(" ");
            if (!string.Equals(text, url, StringComparison.Ordinal)) r.Write("<span class=\"ext-url\">").Write(E(url)).Write("</span>");
            r.Write("</span>");
        }
        else
        {
            r.Write("<a class=\"ext-link\" href=\"").Write(E(url)).Write("\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">")
             .Write(E(text)).Write("</a>");
        }
    }

    sealed class WikiRenderer(Options o) : HtmlObjectRenderer<WikiLinkInline>
    {
        protected override void Write(HtmlRenderer r, WikiLinkInline link)
        {
            var resolved = o.Resolve(link.Target);
            var text = link.DisplayText;
            if (resolved is not null)
            {
                var cls = o.Chat ? "cite" : "wl";
                r.Write("<a class=\"").Write(cls).Write("\" href=\"").Write(E(PageHref(resolved))).Write("\" title=\"")
                 .Write(E(KnowledgePath.StripExtension(resolved))).Write("\">");
                if (o.Chat) r.Write("<span class=\"mi\" aria-hidden=\"true\">description</span>");
                r.Write(E(text)).Write("</a>");
            }
            else
            {
                r.Write("<a class=\"wu\" href=\"").Write(E(CreateHref(link.Target, o.CurrentPath))).Write("\" title=\"Unresolved: create ")
                 .Write(E(link.Target)).Write("\"><span class=\"mi\" aria-hidden=\"true\">link_off</span>")
                 .Write(E(text)).Write("<span class=\"sr-only\"> (unresolved)</span></a>");
            }
        }
    }

    sealed class SafeLinkRenderer(Options o) : HtmlObjectRenderer<LinkInline>
    {
        protected override void Write(HtmlRenderer r, LinkInline link)
        {
            var label = PlainLabel(link);
            var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";

            if (link.IsImage)
            {
                r.Write("<span class=\"img-placeholder\" title=\"Images are not shown\"><span class=\"mi\" aria-hidden=\"true\">image</span>")
                 .Write(E(label.Length > 0 ? label : "image")).Write("</span>");
                return;
            }

            if (url.StartsWith('#'))
            {
                r.Write("<span class=\"anchor-link\">").Write(E(label)).Write("</span>");
                return;
            }

            if (MarkdownPageParser.IsExternal(url))
            {
                WriteExternal(r, label.Length > 0 ? label : url, url, o);
                return;
            }

            if (url.StartsWith("./?p=", StringComparison.Ordinal) || url.StartsWith("?p=", StringComparison.Ordinal))
            {
                r.Write("<a class=\"wl\" href=\"").Write(E(url.StartsWith('?') ? "./" + url : url)).Write("\">").Write(E(label)).Write("</a>");
                return;
            }

            var resolved = o.ResolveRelative?.Invoke(url);
            if (resolved is not null)
                r.Write("<a class=\"wl\" href=\"").Write(E(PageHref(resolved))).Write("\">").Write(E(label)).Write("</a>");
            else
                r.Write("<span class=\"wu-text\" title=\"Unresolved link: ").Write(E(url)).Write("\">").Write(E(label)).Write("</span>");
        }

        static string PlainLabel(ContainerInline container)
        {
            var parts = new List<string>();
            foreach (var child in container.Descendants<Inline>())
            {
                if (child is LiteralInline lit) parts.Add(lit.Content.ToString());
                else if (child is CodeInline code) parts.Add(code.Content);
                else if (child is WikiLinkInline w) parts.Add(w.DisplayText);
            }
            return string.Concat(parts);
        }
    }

    sealed class SafeAutolinkRenderer(Options o) : HtmlObjectRenderer<AutolinkInline>
    {
        protected override void Write(HtmlRenderer r, AutolinkInline link)
        {
            var url = link.IsEmail ? "mailto:" + link.Url : link.Url;
            WriteExternal(r, link.Url, url, o);
        }
    }
}
