using OpenConnect.LiveUI.Compiler;

if (args.Length is not (4 or 6) || args[0] != "--root" || args[2] != "--output" || (args.Length == 6 && args[4] != "--build-id"))
{
    Console.Error.WriteLine("Usage: OpenConnect.LiveUI.Compiler --root <razor-project-directory> --output <manifest.json> [--build-id <id>]");
    return 2;
}

var result = new RazorLiveUIManifestCompiler().Compile(args[1], args[3], args.Length == 6 ? args[5] : null);
foreach (var diagnostic in result.Diagnostics)
    Console.Error.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
return result.Success ? 0 : 1;
