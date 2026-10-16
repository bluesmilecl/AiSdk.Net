using System.Net;
using System.Text;
using System.Text.Json;

namespace AiSdk.Artifacts;

/// <summary>
/// Convierte un artifact de texto en una página HTML autónoma para mostrarlo en un iframe o una pestaña
/// (Markdown con marked, diagramas con Mermaid, componentes React con Babel, CSV como tabla...).
/// </summary>
public static class ArtifactRenderer
{
    private const string Marked = "https://cdn.jsdelivr.net/npm/marked@12.0.2/marked.min.js";
    private const string Mermaid = "https://cdn.jsdelivr.net/npm/mermaid@10.9.1/dist/mermaid.min.js";
    private const string React = "https://unpkg.com/react@18.3.1/umd/react.production.min.js";
    private const string ReactDom = "https://unpkg.com/react-dom@18.3.1/umd/react-dom.production.min.js";
    private const string Babel = "https://unpkg.com/@babel/standalone@7.24.7/babel.min.js";
    private const string Tailwind = "https://cdn.tailwindcss.com/3.4.5";

    /// <summary>Política CSP recomendada al servir artifacts: origen aislado, scripts permitidos.</summary>
    public const string ContentSecurityPolicy = "sandbox allow-scripts allow-forms allow-popups allow-modals allow-downloads";

    public static string ToHtml(Artifact artifact)
    {
        var title = WebUtility.HtmlEncode(artifact.Title);
        var json = JsonSerializer.Serialize(artifact.Content).Replace("</", "<\\/", StringComparison.Ordinal);
        return artifact.Type switch
        {
            ArtifactTypes.Html => artifact.Content,
            ArtifactTypes.Svg => Page(title, $"<div class=\"center\">{artifact.Content}</div>"),
            ArtifactTypes.Markdown => Page(title, $"<article id=\"md\" class=\"prose\"></article><script src=\"{Marked}\"></script><script>document.getElementById('md').innerHTML = marked.parse({json});</script>"),
            ArtifactTypes.Mermaid => Page(title, $"<pre class=\"mermaid\">{WebUtility.HtmlEncode(artifact.Content)}</pre><script src=\"{Mermaid}\"></script><script>mermaid.initialize({{ startOnLoad: true, theme: matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'default' }});</script>"),
            ArtifactTypes.React => ReactPage(title, artifact.Content),
            ArtifactTypes.Csv => Page(title, CsvTable(artifact.Content)),
            _ => Page(title, $"<pre><code class=\"language-{WebUtility.HtmlEncode(artifact.Language ?? artifact.Type)}\">{WebUtility.HtmlEncode(artifact.Content)}</code></pre>"),
        };
    }

    private static string Page(string title, string body) =>
        $$"""
        <!doctype html>
        <html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{title}}</title>
        <style>
          :root { color-scheme: light dark; --bg: #fff; --fg: #1f2328; --muted: #f6f8fa; --line: #d0d7de; }
          @media (prefers-color-scheme: dark) { :root { --bg: #0d1117; --fg: #e6edf3; --muted: #161b22; --line: #30363d; } }
          body { margin: 0; padding: 24px 16px; background: var(--bg); color: var(--fg); font: 16px/1.6 system-ui, sans-serif; }
          .prose, pre, table { max-width: 860px; margin: 0 auto; }
          pre { background: var(--muted); padding: 16px; border-radius: 8px; overflow-x: auto; font: 14px/1.5 ui-monospace, monospace; }
          table { border-collapse: collapse; width: 100%; } td, th { border: 1px solid var(--line); padding: 6px 10px; text-align: left; }
          th { background: var(--muted); } .center { display: grid; place-items: center; } img, svg { max-width: 100%; height: auto; }
        </style></head>
        <body>{{body}}</body></html>
        """;

    private static string ReactPage(string title, string source)
    {
        // Las importaciones de 'react' se sustituyen por los globales del UMD.
        var code = string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith("import ", StringComparison.Ordinal)));
        code = code.Replace("export default function", "window.__Artifact = function", StringComparison.Ordinal)
                   .Replace("export default", "window.__Artifact =", StringComparison.Ordinal);
        var json = JsonSerializer.Serialize(code).Replace("</", "<\\/", StringComparison.Ordinal);
        return $$"""
            <!doctype html>
            <html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{title}}</title>
            <script src="{{Tailwind}}"></script>
            <script src="{{React}}"></script><script src="{{ReactDom}}"></script><script src="{{Babel}}"></script>
            </head><body><div id="root"></div>
            <script>
              const { useState, useEffect, useMemo, useRef, useCallback, useReducer, Fragment } = React;
              try {
                const compiled = Babel.transform({{json}}, { presets: ['react'] }).code;
                new Function('React', 'useState', 'useEffect', 'useMemo', 'useRef', 'useCallback', 'useReducer', 'Fragment', compiled)
                  (React, useState, useEffect, useMemo, useRef, useCallback, useReducer, Fragment);
                ReactDOM.createRoot(document.getElementById('root')).render(React.createElement(window.__Artifact));
              } catch (e) {
                document.getElementById('root').innerHTML = '<pre style="color:#b00;padding:16px">' + String(e).replace(/</g, '&lt;') + '</pre>';
              }
            </script></body></html>
            """;
    }

    private static string CsvTable(string csv)
    {
        var rows = csv.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder("<table>");
        for (var i = 0; i < rows.Length; i++)
        {
            var tag = i == 0 ? "th" : "td";
            builder.Append("<tr>");
            foreach (var cell in rows[i].Split(','))
            {
                builder.Append('<').Append(tag).Append('>').Append(WebUtility.HtmlEncode(cell.Trim().Trim('"'))).Append("</").Append(tag).Append('>');
            }

            builder.Append("</tr>");
        }

        return builder.Append("</table>").ToString();
    }
}
