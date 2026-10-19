using MarkItDown.NET;
using MarkItDown.NET.Converters;
using MarkItDown.NET.Worker;

if (args is ["--protocol"])
{
    Console.WriteLine("2");
    return 0;
}

if (args is ["--render-pdf", _, _, _, _])
    return PdfConverter.RunRenderSubprocess(args);

if (args.Length < 3 || args[0] != "convert")
{
    Console.Error.WriteLine("Usage: convert <input-path> <new-output-directory> [--filename <original-name>] [--azure]");
    return 2;
}
string? filename = null;
var cloud = false;
for (var index = 3; index < args.Length; index++)
{
    if (args[index] == "--azure") cloud = true;
    else if (args[index] == "--filename" && index + 1 < args.Length) filename = args[++index];
    else { Console.Error.WriteLine("Unknown or incomplete option."); return 2; }
}
DocumentConverterOptions? options = null;
if (cloud)
{
    var endpoint = Environment.GetEnvironmentVariable("MARKITDOWN_AZURE_DI_ENDPOINT");
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
        uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
    {
        Console.Error.WriteLine("Explicit Azure conversion requires a valid HTTPS MARKITDOWN_AZURE_DI_ENDPOINT.");
        return 2;
    }
    options = new()
    {
        AzureDiEndpoint = endpoint,
        AzureDiApiKey = Environment.GetEnvironmentVariable("MARKITDOWN_AZURE_DI_API_KEY"),
        PdfRenderWorkerPath = typeof(ConversionWorker).Assembly.Location
    };
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
try
{
    await ConversionWorker.ConvertAsync(args[1], args[2], filename, options, cancellation.Token);
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Conversion cancelled."); return 5; }
catch (PdfTextUnavailableException exception) { Console.Error.WriteLine(exception.Message); return 6; }
catch (NotSupportedException) { Console.Error.WriteLine("The document format is not enabled."); return 3; }
catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or
    FormatException or Azure.RequestFailedException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
{
    Console.Error.WriteLine($"Conversion failed: {exception.GetType().Name}.");
    return 4;
}
