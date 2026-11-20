using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Components;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Compiler;

public enum LiveUICompilerDiagnosticSeverity { Info, Warning, Error }

public sealed record LiveUICompilerDiagnostic(string Code, LiveUICompilerDiagnosticSeverity Severity, string File, string Message);
public sealed record LiveUIManifestCompilationResult(bool Success, LiveUIManifest? Manifest, IReadOnlyList<LiveUICompilerDiagnostic> Diagnostics);

/// <summary>Builds a deterministic LiveUI manifest by analyzing Razor's component intermediate representation.</summary>
public sealed class RazorLiveUIManifestCompiler
{
    private static readonly JsonSerializerOptions JsonOptions = CreateCompilerJsonOptions();
    private static readonly Regex SensitiveFieldPattern = new(
        "password|passwd|secret|token|api[\\s_-]?key|\\bssn\\b|social[\\s._-]?security|credit.?card|credit|card|cvv|cvc|credential|private[\\s._-]?key|\\bpin\\b|\\bpin[\\s_-]?(?:code|number)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public LiveUIManifestCompilationResult Compile(string projectDirectory, string outputPath, string? buildId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var root = Path.GetFullPath(projectDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Razor project directory does not exist: {root}");

        var files = Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var diagnostics = new List<LiveUICompilerDiagnostic>();
        if (files.Length == 0)
            return new(false, null, [new("LUI1000", LiveUICompilerDiagnosticSeverity.Error, root, "No Razor components were found.")]);

        var fileSystem = RazorProjectFileSystem.Create(root);
        var engine = RazorProjectEngine.Create(RazorConfiguration.Default, fileSystem, builder => { });
        var parsed = new Dictionary<string, ParsedComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var projectItem = fileSystem.GetItem("/" + relative);
            var document = engine.Process(projectItem);
            var intermediate = document.GetDocumentIntermediateNode();
            var routeNode = Descendants(intermediate).FirstOrDefault(node => node.GetType().Name == "RouteAttributeExtensionNode");
            var route = GetProperty(routeNode, "Template") is { } template ? GetProperty(template, "Value")?.ToString() : null;
            var razorDiagnostics = RazorCodeDocumentExtensions.GetSyntaxTree(document).Diagnostics;
            foreach (var diagnostic in razorDiagnostics)
                diagnostics.Add(new(diagnostic.Id, diagnostic.Severity == RazorDiagnosticSeverity.Error ? LiveUICompilerDiagnosticSeverity.Error : LiveUICompilerDiagnosticSeverity.Warning,
                    relative, diagnostic.GetMessage()));
            parsed[relative] = new(relative, route, intermediate, File.ReadAllText(file));
        }

        var routeComponents = parsed.Values.Where(component => component.Route is not null).OrderBy(component => component.Route, StringComparer.Ordinal).ToArray();
        if (routeComponents.Length == 0)
            diagnostics.Add(new("LUI1001", LiveUICompilerDiagnosticSeverity.Error, root, "No statically declared @page route was found."));

        var routes = new List<LiveUIRoute>();
        foreach (var page in routeComponents)
        {
            var routeId = page.Route == "/" ? "route.root" : "route." + Slug(page.Route!);
            var componentSet = DiscoverComponentGraph(page, parsed);
            var title = ReadPageTitle(page.Root);
            var targets = new List<LiveUITarget>();
            var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var component in componentSet)
            {
                var componentElements = DescendantsWithAncestors(component.Root, Array.Empty<IntermediateNode>())
                    .Where(item => item.Node.GetType().Name == "MarkupElementIntermediateNode").ToArray();
                var regionIds = new Dictionary<IntermediateNode, string>();
                foreach (var item in componentElements)
                {
                    foreach (var descriptor in Describe(item.Node, component.Source))
                    {
                        var key = Slug($"{component.File}:{descriptor.Kind}:{descriptor.Label}");
                        idCounts.TryGetValue(key, out var count);
                        idCounts[key] = count + 1;
                        var id = $"{routeId}.{key}" + (count == 0 ? string.Empty : $".{count + 1}");
                        if (count > 0)
                            diagnostics.Add(new("LUI1002", LiveUICompilerDiagnosticSeverity.Warning, component.File,
                                $"Repeated {descriptor.Kind} target '{descriptor.Label}' is ambiguous at runtime."));
                        var parentId = item.Ancestors.Reverse()
                            .Select(ancestor => regionIds.TryGetValue(ancestor, out var parent) ? parent : null)
                            .FirstOrDefault(parent => parent is not null);
                        targets.Add(new(id, descriptor.Kind, descriptor.Label, descriptor.Capabilities, descriptor.Role,
                            Selector: descriptor.Selector, ParentId: parentId, RequiresConfirmation: descriptor.RequiresConfirmation));
                        if (descriptor.Kind == LiveUITargetKind.Region && descriptor.Selector is not null)
                            regionIds[item.Node] = id;
                    }
                }
                foreach (var block in Descendants(component.Root).Where(node => node.GetType().Name == "MarkupBlockIntermediateNode"))
                {
                    var raw = GetProperty(block, "Content")?.ToString();
                    if (raw is null)
                        continue;
                    foreach (var element in ParseRawMarkup(raw))
                    {
                        if (char.IsUpper(element.Tag[0]))
                            continue;
                        foreach (var descriptor in DescribeRaw(element))
                        {
                            var key = Slug($"{component.File}:{descriptor.Kind}:{descriptor.Label}");
                            idCounts.TryGetValue(key, out var count);
                            idCounts[key] = count + 1;
                            var id = $"{routeId}.{key}" + (count == 0 ? string.Empty : $".{count + 1}");
                            if (count > 0)
                                diagnostics.Add(new("LUI1002", LiveUICompilerDiagnosticSeverity.Warning, component.File,
                                    $"Repeated {descriptor.Kind} target '{descriptor.Label}' is ambiguous at runtime."));
                            targets.Add(new(id, descriptor.Kind, descriptor.Label, descriptor.Capabilities, descriptor.Role,
                                Selector: descriptor.Selector, RequiresConfirmation: descriptor.RequiresConfirmation));
                        }
                    }
                }
            }

            var duplicates = targets.GroupBy(target => (target.Kind, target.Label), new KindLabelComparer()).Where(group => group.Count() > 1);
            foreach (var duplicate in duplicates)
                diagnostics.Add(new("LUI1003", LiveUICompilerDiagnosticSeverity.Warning, page.File,
                    $"Route '{page.Route}' contains {duplicate.Count()} targets labeled '{duplicate.Key.Label}' with kind '{duplicate.Key.Kind}'."));
            routes.Add(new(routeId, page.Route!, title, targets.OrderBy(target => target.Id, StringComparer.Ordinal).ToArray()));
        }

        var fingerprint = buildId ?? ComputeBuildId(root, files);
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, fingerprint, string.Empty, routes.OrderBy(route => route.Path, StringComparer.Ordinal).ToArray());
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        diagnostics.AddRange(new LiveUIManifestValidator().Validate(manifest)
            .Select(code => new LiveUICompilerDiagnostic(code, LiveUICompilerDiagnosticSeverity.Error, root, "Generated manifest failed validation.")));
        if (diagnostics.Any(diagnostic => diagnostic.Severity == LiveUICompilerDiagnosticSeverity.Error))
            return new(false, manifest, diagnostics);

        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        return new(true, manifest, diagnostics);
    }

    private static IReadOnlyList<ParsedComponent> DiscoverComponentGraph(ParsedComponent page, IReadOnlyDictionary<string, ParsedComponent> parsed)
    {
        var discovered = new List<ParsedComponent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ParsedComponent>();
        queue.Enqueue(page);
        while (queue.TryDequeue(out var component))
        {
            if (!seen.Add(component.File))
                continue;
            discovered.Add(component);
            foreach (var name in Descendants(component.Root).Where(node => node.GetType().Name == "MarkupElementIntermediateNode")
                         .Select(element => GetProperty(element, "TagName")?.ToString() ?? string.Empty)
                         .Where(tag => tag.Length > 0 && char.IsUpper(tag[0]))
                         .Distinct(StringComparer.Ordinal))
            {
                var child = parsed.Values.FirstOrDefault(candidate => Path.GetFileNameWithoutExtension(candidate.File).Equals(name, StringComparison.Ordinal));
                if (child is not null)
                    queue.Enqueue(child);
            }
        }
        return discovered;
    }

    private static string? ReadPageTitle(IntermediateNode root)
    {
        var pageTitle = Descendants(root).FirstOrDefault(element => element.GetType().Name == "MarkupElementIntermediateNode" &&
            string.Equals(GetProperty(element, "TagName")?.ToString(), "PageTitle", StringComparison.OrdinalIgnoreCase));
        return pageTitle is null ? null : NormalizeText(ReadStaticText(GetChildren(GetProperty(pageTitle, "Body"))));
    }

    private static IEnumerable<TargetDescriptor> Describe(IntermediateNode element, string source)
    {
        var tag = GetProperty(element, "TagName")?.ToString() ?? string.Empty;
        var lower = tag.ToLowerInvariant();
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var attrNodes = GetChildren(GetProperty(element, "Attributes"));
        foreach (var attribute in attrNodes)
        {
            var name = GetProperty(attribute, "AttributeName")?.ToString();
            if (name is not null && !attributes.ContainsKey(name))
                attributes[name] = ReadStaticValue(attribute, source);
        }

        var label = First(attributes, "aria-label", "label", "text", "placeholder", "title");
        var text = NormalizeText(ReadStaticText(GetChildren(GetProperty(element, "Body"))));
        if (string.IsNullOrWhiteSpace(label) && (lower is "button" or "a" or "summary" or "mudbutton" or "mudiconbutton"))
            label = text;
        var explicitTarget = First(attributes, "data-oc-target");
        var requiresConfirmation = attrNodes.Any(attribute =>
        {
            var name = GetProperty(attribute, "AttributeName")?.ToString();
            return name is not null &&
                   (name.Equals("OnClick", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ValueChanged", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("OnValidSubmit", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("OnSubmit", StringComparison.OrdinalIgnoreCase));
        });

        if (lower is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
        {
            if (!string.IsNullOrWhiteSpace(text))
                yield return new(LiveUITargetKind.Heading, text, "heading", Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll));
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(explicitTarget))
        {
            var escaped = explicitTarget.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            yield return new(LiveUITargetKind.Region, string.IsNullOrWhiteSpace(label) ? explicitTarget : label, "region",
                Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll, LiveUICapability.WaitForState), $"[data-oc-target=\"{escaped}\"]");
        }

        switch (lower)
        {
            case "button":
            case "mudbutton":
            case "mudiconbutton":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Button, label, "button", Caps(LiveUICapability.Activate, LiveUICapability.Highlight), RequiresConfirmation: requiresConfirmation);
                yield break;
            case "a":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Link, label, "link", Caps(LiveUICapability.Activate, LiveUICapability.Highlight));
                yield break;
            case "input":
            case "textarea":
            case "mudtextfield":
                var rawInputType = First(attributes, "type", "InputType");
                if (HasUnresolvedInputType(attributes))
                    yield break;
                var inputType = NormalizeInputType(rawInputType);
                if (IsSensitiveInput(inputType, label, attributes))
                    yield break;
                if (inputType == "checkbox")
                {
                    if (!string.IsNullOrWhiteSpace(label))
                        yield return new(LiveUITargetKind.Checkbox, label, "checkbox", Caps(LiveUICapability.Select, LiveUICapability.Highlight), RequiresConfirmation: requiresConfirmation);
                    yield break;
                }
                if (!string.IsNullOrWhiteSpace(label))
                {
                    var filtering = inputType == "search" || label.Contains("search", StringComparison.OrdinalIgnoreCase);
                    var capabilities = new HashSet<LiveUICapability> { LiveUICapability.Focus, LiveUICapability.SetValue, LiveUICapability.Highlight, LiveUICapability.Inspect };
                    if (filtering)
                        capabilities.Add(LiveUICapability.Filter);
                    yield return new(LiveUITargetKind.Input, label, inputType == "search" ? "searchbox" : "textbox", capabilities);
                }
                yield break;
            case "select":
            case "mudselect":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Select, label, "combobox", Caps(LiveUICapability.Focus, LiveUICapability.Select, LiveUICapability.Highlight));
                yield break;
            case "mudcheckbox":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Checkbox, label, "checkbox", Caps(LiveUICapability.Select, LiveUICapability.Highlight), RequiresConfirmation: requiresConfirmation);
                yield break;
            case "mudtabspanel":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Tab, label, "tab", Caps(LiveUICapability.Activate, LiveUICapability.Highlight, LiveUICapability.Filter, LiveUICapability.WaitForState));
                yield break;
            case "mudtabs":
                yield break;
            case "nav":
                yield return new(LiveUITargetKind.Region, string.IsNullOrWhiteSpace(label) ? "Navigation" : label, "navigation", Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll));
                yield break;
            case "main":
                yield return new(LiveUITargetKind.Region, string.IsNullOrWhiteSpace(label) ? "Main content" : label, "main", Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll));
                yield break;
            case "ul":
            case "ol":
            case "mudlist":
                yield return new(LiveUITargetKind.List, string.IsNullOrWhiteSpace(label) ? "Task list" : label, "list",
                    Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll, LiveUICapability.WaitForState), ".mud-list");
                yield break;
            case "table":
            case "mudtable":
            case "muddatagrid":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Table, label, "table", Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll, LiveUICapability.WaitForState));
                yield break;
            case "details":
            case "mudexpansionpanel":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Region, label, "group", Caps(LiveUICapability.Activate, LiveUICapability.Highlight, LiveUICapability.Inspect));
                yield break;
        }

        if (tag.Length > 0 && char.IsUpper(tag[0]))
        {
            if (tag.Equals("MudTabPanel", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(label))
                yield return new(LiveUITargetKind.Tab, label, "tab", Caps(LiveUICapability.Activate, LiveUICapability.Highlight, LiveUICapability.Filter, LiveUICapability.WaitForState));
            else if (tag.Equals("MudCheckBox", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(label))
                yield return new(LiveUITargetKind.Checkbox, label, "checkbox", Caps(LiveUICapability.Select, LiveUICapability.Highlight));
            else if ((tag.Equals("MudTable", StringComparison.OrdinalIgnoreCase) || tag.Equals("MudDataGrid", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(label))
                yield return new(LiveUITargetKind.Table, label, "table", Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll, LiveUICapability.WaitForState));
            else if (tag.Equals("MudList", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(label))
                yield return new(LiveUITargetKind.List, label, "list", Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll, LiveUICapability.WaitForState));
        }
    }

    private static string? ReadStaticValue(IntermediateNode attribute, string source)
    {
        if (GetProperty(attribute, "AttributeNameExpression") is not null ||
            Descendants(attribute).Any(node => node.GetType().Name.StartsWith("CSharp", StringComparison.Ordinal)))
            return null;
        var span = GetProperty(attribute, "Source");
        var start = GetProperty(span, "AbsoluteIndex") as int?;
        var length = GetProperty(span, "Length") as int?;
        var value = start is not null && length is not null && start >= 0 && length >= 0 && start + length <= source.Length
            ? ReadAttributeValue(source.Substring(start.Value, length.Value))
            : ReadStaticText(attribute.Children).Trim();
        return value.Length == 0 ? null : value;
    }

    private static string ReadAttributeValue(string source)
    {
        var equals = source.IndexOf('=');
        if (equals < 0)
            return string.Empty;
        var value = source[(equals + 1)..].Trim();
        if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
            value = value[1..^1];
        return NormalizeText(value);
    }

    private static IEnumerable<RawMarkupElement> ParseRawMarkup(string markup)
    {
        var position = 0;
        while ((position = markup.IndexOf('<', position)) >= 0)
        {
            if (markup.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = markup.IndexOf("-->", position + 4, StringComparison.Ordinal);
                position = commentEnd < 0 ? markup.Length : commentEnd + 3;
                continue;
            }

            var cursor = position + 1;
            if (cursor >= markup.Length || markup[cursor] is '/' or '!' or '?')
            {
                position = cursor;
                continue;
            }

            var nameStart = cursor;
            while (cursor < markup.Length && IsTagCharacter(markup[cursor]))
                cursor++;
            if (cursor == nameStart)
            {
                position = cursor;
                continue;
            }

            var tag = markup[nameStart..cursor];
            var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var selfClosing = false;
            while (cursor < markup.Length)
            {
                SkipWhitespace(markup, ref cursor);
                if (cursor >= markup.Length)
                    break;
                if (markup[cursor] == '>')
                {
                    cursor++;
                    break;
                }
                if (markup[cursor] == '/')
                {
                    selfClosing = true;
                    cursor++;
                    continue;
                }
                var attributeStart = cursor;
                while (cursor < markup.Length && !char.IsWhiteSpace(markup[cursor]) && markup[cursor] is not '=' and not '>' and not '/')
                    cursor++;
                if (cursor == attributeStart)
                {
                    cursor++;
                    continue;
                }
                var name = markup[attributeStart..cursor];
                SkipWhitespace(markup, ref cursor);
                string? value = null;
                if (cursor < markup.Length && markup[cursor] == '=')
                {
                    cursor++;
                    SkipWhitespace(markup, ref cursor);
                    if (cursor < markup.Length && markup[cursor] is '"' or '\'')
                    {
                        var quote = markup[cursor++];
                        var valueStart = cursor;
                        while (cursor < markup.Length && markup[cursor] != quote)
                            cursor++;
                        value = markup[valueStart..cursor];
                        if (cursor < markup.Length)
                            cursor++;
                    }
                    else
                    {
                        var valueStart = cursor;
                        while (cursor < markup.Length && !char.IsWhiteSpace(markup[cursor]) && markup[cursor] != '>')
                            cursor++;
                        value = markup[valueStart..cursor];
                    }
                }
                attributes.TryAdd(name, value is null ? null : WebUtility.HtmlDecode(value));
            }

            var text = string.Empty;
            if (!selfClosing && !IsVoidElement(tag))
            {
                var closingTag = "</" + tag;
                var closeStart = markup.IndexOf(closingTag, cursor, StringComparison.OrdinalIgnoreCase);
                if (closeStart >= 0)
                {
                    text = StripMarkup(markup[cursor..closeStart]);
                    var closeEnd = markup.IndexOf('>', closeStart);
                    position = closeEnd < 0 ? closeStart + closingTag.Length : closeEnd + 1;
                }
                else
                    position = cursor;
            }
            else
                position = cursor;

            yield return new(tag, attributes, text);
        }
    }

    private static IEnumerable<TargetDescriptor> DescribeRaw(RawMarkupElement element)
    {
        var lower = element.Tag.ToLowerInvariant();
        var label = First(element.Attributes, "aria-label", "placeholder", "title");
        if (string.IsNullOrWhiteSpace(label) && lower is "button" or "a" or "summary")
            label = element.Text;
        var requiresConfirmation = element.Attributes.Keys.Any(name =>
            name.Equals("onclick", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("onsubmit", StringComparison.OrdinalIgnoreCase));
        switch (lower)
        {
            case "button":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Button, label, "button", Caps(LiveUICapability.Activate, LiveUICapability.Highlight), RequiresConfirmation: requiresConfirmation);
                break;
            case "a":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Link, label, "link", Caps(LiveUICapability.Activate, LiveUICapability.Highlight));
                break;
            case "input":
            case "textarea":
                var type = NormalizeInputType(First(element.Attributes, "type"));
                if (type?.StartsWith('@') == true)
                    break;
                if (IsSensitiveInput(type, label, element.Attributes))
                    break;
                if (type == "checkbox")
                {
                    if (!string.IsNullOrWhiteSpace(label))
                        yield return new(LiveUITargetKind.Checkbox, label, "checkbox", Caps(LiveUICapability.Select, LiveUICapability.Highlight));
                    break;
                }
                if (!string.IsNullOrWhiteSpace(label))
                {
                    var capabilities = new HashSet<LiveUICapability> { LiveUICapability.Focus, LiveUICapability.SetValue, LiveUICapability.Highlight, LiveUICapability.Inspect };
                    if (type == "search" || label.Contains("search", StringComparison.OrdinalIgnoreCase))
                        capabilities.Add(LiveUICapability.Filter);
                    yield return new(LiveUITargetKind.Input, label, type == "search" ? "searchbox" : "textbox", capabilities);
                }
                break;
            case "select":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Select, label, "combobox", Caps(LiveUICapability.Focus, LiveUICapability.Select, LiveUICapability.Highlight));
                break;
            case "nav":
                yield return new(LiveUITargetKind.Region, string.IsNullOrWhiteSpace(label) ? "Navigation" : label, "navigation", Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll));
                break;
            case "main":
                yield return new(LiveUITargetKind.Region, string.IsNullOrWhiteSpace(label) ? "Main content" : label, "main", Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll));
                break;
            case "ul":
            case "ol":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.List, label, "list", Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll, LiveUICapability.WaitForState));
                break;
            case "table":
                if (!string.IsNullOrWhiteSpace(label))
                    yield return new(LiveUITargetKind.Table, label, "table", Caps(LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll, LiveUICapability.WaitForState));
                break;
        }
    }

    private static string StripMarkup(string content)
    {
        var result = new StringBuilder(content.Length);
        var inTag = false;
        foreach (var character in content)
        {
            if (character == '<')
                inTag = true;
            else if (character == '>')
                inTag = false;
            else if (!inTag)
                result.Append(character);
        }
        return NormalizeText(WebUtility.HtmlDecode(result.ToString()));
    }

    private static string NormalizeText(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsTagCharacter(char character) => char.IsLetterOrDigit(character) || character is '-' or ':' or '.';
    private static bool IsVoidElement(string tag) => tag.Equals("input", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("img", StringComparison.OrdinalIgnoreCase) || tag.Equals("br", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("hr", StringComparison.OrdinalIgnoreCase) || tag.Equals("meta", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("link", StringComparison.OrdinalIgnoreCase) || tag.Equals("area", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("base", StringComparison.OrdinalIgnoreCase) || tag.Equals("embed", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("source", StringComparison.OrdinalIgnoreCase) || tag.Equals("track", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("wbr", StringComparison.OrdinalIgnoreCase) || tag.Equals("col", StringComparison.OrdinalIgnoreCase);

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
    }

    private static string ReadStaticText(IEnumerable<IntermediateNode> nodes)
    {
        var builder = new StringBuilder();
        foreach (var node in nodes)
        {
            if (node.GetType().Name.EndsWith("IntermediateToken", StringComparison.Ordinal))
                builder.Append(GetProperty(node, "Content")?.ToString());
            else if (!node.GetType().Name.StartsWith("CSharp", StringComparison.Ordinal))
                builder.Append(ReadStaticText(node.Children));
        }
        return builder.ToString();
    }

    private static string? First(IReadOnlyDictionary<string, string?> attributes, params string[] names) =>
        names.Select(name => attributes.TryGetValue(name, out var value) ? value : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool IsSensitiveInput(string? inputType, string? label, IReadOnlyDictionary<string, string?> attributes)
    {
        var normalizedType = NormalizeInputType(inputType);
        if (normalizedType is "password" or "email" or "tel" or "number" or "hidden" or "file")
            return true;

        var autocomplete = First(attributes, "autocomplete");
        if (autocomplete?.StartsWith("cc-", StringComparison.OrdinalIgnoreCase) == true ||
            string.Equals(autocomplete, "current-password", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(autocomplete, "new-password", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(autocomplete, "one-time-code", StringComparison.OrdinalIgnoreCase))
            return true;

        var metadata = string.Join(" ", label, First(attributes,
            "name", "id", "aria-label", "aria-labelledby", "placeholder", "title", "label", "autocomplete"));
        return SensitiveFieldPattern.IsMatch(metadata);
    }

    private static string? NormalizeInputType(string? inputType) =>
        inputType?.Split('.').Last().ToLowerInvariant();

    private static bool HasUnresolvedInputType(IReadOnlyDictionary<string, string?> attributes) =>
        attributes.Any(attribute =>
            (attribute.Key.Equals("type", StringComparison.OrdinalIgnoreCase) ||
             attribute.Key.Equals("InputType", StringComparison.OrdinalIgnoreCase)) &&
            (attribute.Value is null || attribute.Value.TrimStart().StartsWith('@')));

    private static IReadOnlySet<LiveUICapability> Caps(params LiveUICapability[] capabilities) => new SortedSet<LiveUICapability>(capabilities);

    private static JsonSerializerOptions CreateCompilerJsonOptions()
    {
        var options = LiveUIManifestJson.CreateSerializerOptions();
        options.WriteIndented = true;
        return options;
    }

    private static IEnumerable<IntermediateNode> Descendants(IntermediateNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static IEnumerable<(IntermediateNode Node, IReadOnlyList<IntermediateNode> Ancestors)> DescendantsWithAncestors(
        IntermediateNode node, IReadOnlyList<IntermediateNode> ancestors)
    {
        foreach (var child in node.Children)
        {
            yield return (child, ancestors);
            var childAncestors = ancestors.Append(child).ToArray();
            foreach (var descendant in DescendantsWithAncestors(child, childAncestors))
                yield return descendant;
        }
    }

    private static IEnumerable<IntermediateNode> GetChildren(object? value) =>
        value is IEnumerable<IntermediateNode> nodes ? nodes : [];

    private static object? GetProperty(object? instance, string name) =>
        instance?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

    private static string ComputeBuildId(string root, IEnumerable<string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/')));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var separator = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separator && builder.Length > 0)
                    builder.Append('-');
                builder.Append(character);
                separator = false;
            }
            else
                separator = true;
        }
        return builder.Length == 0 ? "unnamed" : builder.ToString();
    }

    private sealed record ParsedComponent(string File, string? Route, IntermediateNode Root, string Source);
    private sealed record RawMarkupElement(string Tag, IReadOnlyDictionary<string, string?> Attributes, string Text);
    private sealed record TargetDescriptor(
        LiveUITargetKind Kind,
        string Label,
        string Role,
        IReadOnlySet<LiveUICapability> Capabilities,
        string? Selector = null,
        bool RequiresConfirmation = false);
    private sealed class KindLabelComparer : IEqualityComparer<(LiveUITargetKind Kind, string Label)>
    {
        public bool Equals((LiveUITargetKind Kind, string Label) left, (LiveUITargetKind Kind, string Label) right) => left.Kind == right.Kind && string.Equals(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((LiveUITargetKind Kind, string Label) value) => HashCode.Combine(value.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Label));
    }
}
