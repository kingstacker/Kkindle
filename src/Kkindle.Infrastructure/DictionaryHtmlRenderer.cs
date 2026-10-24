using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed record DictionaryHtmlPalette(string Paper, string Ink, string Muted, string Soft, string Border);

public static class DictionaryHtmlRenderer
{
    public static string Render(IReadOnlyList<DictionaryEntry> entries, DictionaryHtmlPalette? palette = null)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var body = new StringBuilder();
        foreach (var entry in entries)
        {
            var document = new HtmlDocument();
            document.LoadHtml(entry.Html ?? $"<pre>{WebUtility.HtmlEncode(entry.Definition)}</pre>");
            foreach (var node in document.DocumentNode.Descendants().ToArray())
            {
                if (node.Name is "script" or "link" or "iframe" or "object" or "embed" or "base" or "meta" or "form")
                { node.Remove(); continue; }
                foreach (var attribute in node.Attributes.ToArray())
                {
                    if (attribute.Name.Equals("onclick", StringComparison.OrdinalIgnoreCase))
                    {
                        var action = Regex.Match(attribute.Value, @"^\w+\.(toggle|showAtLink|toggleImg)\(this\);?$");
                        if (action.Success) node.SetAttributeValue("data-dictionary-toggle", action.Groups[1].Value);
                    }
                    if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                        || attribute.Name is "srcdoc" or "action" or "formaction") node.Attributes.Remove(attribute);
                }
                if (node.Name == "a")
                {
                    var href = WebUtility.HtmlDecode(node.GetAttributeValue("href", ""));
                    if (href.StartsWith("entry://", StringComparison.OrdinalIgnoreCase))
                        node.SetAttributeValue("data-dictionary-term", Uri.UnescapeDataString(href[8..].Split('#')[0]));
                    if (!href.StartsWith('#') && !href.StartsWith("entry://", StringComparison.OrdinalIgnoreCase))
                        node.Attributes.Remove("href");
                    // Audio is stored separately in MDD, not in the MDX HTML.
                    if (href.StartsWith("sound:", StringComparison.OrdinalIgnoreCase)) node.Remove();
                }
                if (node.Name == "img" && !node.GetAttributeValue("src", "").StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    node.Remove();
            }
            body.Append("<section class='kkindle-dictionary-entry'><header class='kkindle-dictionary-name'>").Append(WebUtility.HtmlEncode(entry.DictionaryName))
                .Append("</header>").Append(document.DocumentNode.InnerHtml).Append("</section>");
        }
        var css = string.Join("\n", entries.Select(entry => entry.Stylesheet).Where(css => css is not null).Distinct())
            .Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);
        palette ??= new("#ffffff", "#292d29", "#606a60", "#e8ece6", "#cdd4c9");
        static string Color(string value) => Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value : throw new ArgumentException("Invalid dictionary palette color.");
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; script-src 'nonce-{{nonce}}'; base-uri 'none'; form-action 'none'">
            <style>:root{--paper:{{Color(palette.Paper)}};--ink:{{Color(palette.Ink)}};--muted:{{Color(palette.Muted)}};--soft:{{Color(palette.Soft)}};--line:{{Color(palette.Border)}}}
            html,body{margin:0;padding:0;background:var(--paper);color:var(--ink);scrollbar-color:var(--line) var(--paper)}body{padding:10px;font:15px/1.55 sans-serif;overflow-wrap:anywhere}
            section.kkindle-dictionary-entry+section.kkindle-dictionary-entry{border-top:1px solid var(--line);margin-top:14px;padding-top:10px}.kkindle-dictionary-name{font:12px sans-serif;color:var(--muted);margin-bottom:8px}
            pre{white-space:pre-wrap;font:inherit}img{max-width:100%}
            </style><style>{{css}}</style></head><body>{{body}}
            <script nonce="{{nonce}}">
            document.addEventListener('click',event=>{
              const term=event.target.closest('[data-dictionary-term]');
              if(term){event.preventDefault();location.href='kkindle-dictionary://lookup?'+encodeURIComponent(term.dataset.dictionaryTerm);return;}
              const button=event.target.closest('[data-dictionary-toggle]');if(!button)return;
              const action=button.dataset.dictionaryToggle;
              const target=action==='showAtLink'?button.nextElementSibling:action==='toggleImg'?button:button.parentElement.querySelector('.content');
              if(!target)return;
              if(action==='toggleImg'){target.style.maxHeight=target.style.maxHeight==='none'?'4em':'none';return;}
              target.style.display=getComputedStyle(target).display==='none'?'block':'none';
              const arrow=button.parentElement.querySelector('span.arrow');if(arrow)arrow.textContent=target.style.display==='none'?'►':'▼';
            });
            </script></body></html>
            """;
    }
}
