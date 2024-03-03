using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;

var result = new Dictionary<string, Dictionary<string, HashSet<string>>>();

var providers = new List<EventPipeProvider>
{
    new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, (long)ClrTraceEventParser.Keywords.JITSymbols)
};

var fileName = args[0];
var startInfo = new ProcessStartInfo
{
    FileName = fileName,
    UseShellExecute = false,
    WindowStyle = ProcessWindowStyle.Normal,
    WorkingDirectory = Path.GetDirectoryName(fileName),
    Environment =
    {
        ["DOTNET_ReadyToRun"] = "1",
        ["DOTNET_JITMinOpts"] = "1"
    },
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};

foreach (var arg in args[1..])
{
    startInfo.ArgumentList.Add(arg);
}

Console.WriteLine($"Start profiling: {fileName}");
Console.WriteLine("Press Ctrl+C to stop.");

using var process = Process.Start(startInfo);

if (process is null) return 1;

var client = new DiagnosticsClient(process.Id);
using var session = client.StartEventPipeSession(providers, false);
var source = new EventPipeEventSource(session.EventStream);

IObservable<MethodJittingStartedTraceData> jitStartStream = source.Clr.Observe<MethodJittingStartedTraceData>("Method/JittingStarted");
IObservable<MethodLoadUnloadVerboseTraceData> jitEndStream = source.Clr.Observe<MethodLoadUnloadVerboseTraceData>("Method/LoadVerbose");

var jitTimes =
    from start in jitStartStream
    select (MethodID: (ulong)start.MethodID, ModuleID: (ulong)start.ModuleID);

jitTimes.Subscribe((data) => RecordMethodInfo(data.MethodID, data.ModuleID));

void RecordMethodInfo(ulong methodId, ulong moduleId)
{
    using var dataTarget = DataTarget.AttachToProcess(process.Id, true);
    var version = dataTarget.ClrVersions.First();
    using var runtime = version.CreateRuntime();

    var sb = new StringBuilder();
    runtime.FlushCachedData();
    var method = runtime.GetMethodByHandle(methodId);
    var module = runtime.EnumerateModules().FirstOrDefault(i => i.Address == moduleId);
    if (module is not null && method is not null)
    {
        lock (result)
        {
            var assemblyName = Path.GetFileNameWithoutExtension(module.AssemblyName!);
            if (!result.TryGetValue(assemblyName, out var typeRecord))
            {
                result[assemblyName] = typeRecord = [];
            }
            var (typeName, methodName, parameters) = ParseMethodAndType(method.ToString()!);
            if (!FilterTypeAndMethod(method, typeName, methodName))
            {
                return;
            }
            if (!typeRecord.TryGetValue(typeName, out var methodRecord))
            {
                typeRecord[typeName] = methodRecord = [];
            }

            methodRecord.Add(methodName);
        }
    }
}

(string TypeName, string MethodName, string MethodParams) ParseMethodAndType(ReadOnlySpan<char> qualifiedName)
{
    var typeNameBuilder = new StringBuilder();
    var methodNameBuilder = new StringBuilder();
    var lastSegment = "";
    var chunk = new StringBuilder();
    int i = 0;
    int inTypeArgList = 0;
    int splitIndex = 0;
    bool hasGenericArgs = false;
    int typeSkipAfterIndex = -1;
    while (i < qualifiedName.Length)
    {
        switch (qualifiedName[i])
        {
            case '.':
                if (inTypeArgList != 0)
                {
                    chunk.Append(qualifiedName[i]);
                    break;
                }
                lastSegment = chunk.ToString();
                chunk.Clear();
                if (qualifiedName[i..].StartsWith(".ctor(") || qualifiedName[i..].StartsWith(".cctor("))
                {
                    splitIndex = i;
                    i = qualifiedName.Length - 1;
                }
                break;
            case '[':
                inTypeArgList++;
                chunk.Append(qualifiedName[i]);
                hasGenericArgs = true;
                break;
            case ']':
                inTypeArgList--;
                chunk.Append(qualifiedName[i]);
                if (hasGenericArgs && inTypeArgList == 0 && typeSkipAfterIndex == -1)
                {
                    typeSkipAfterIndex = i + 1;
                }
                break;
            case '(':
                splitIndex = i - chunk.Length;
                i = qualifiedName.Length - 1;
                break;
            case ',':
            default:
                chunk.Append(qualifiedName[i]);
                break;
        }

        i++;
    }

    ReadOnlySpan<char> typeSig = "";
    ReadOnlySpan<char> methodSig = "";

    if (splitIndex == 0)
    {
        methodSig = qualifiedName;
    }
    else
    {
        typeSig = qualifiedName[..(splitIndex - 1)];
        methodSig = qualifiedName[splitIndex..];
    }

    var sigSplitIndex = methodSig.IndexOf('(');
    if (typeSkipAfterIndex != -1)
    {
        typeSig = typeSig[..(typeSkipAfterIndex > typeSig.Length ? typeSig.Length : typeSkipAfterIndex)];
    }

    return (typeSig.ToString().Replace("System.__Canon", "System.Object", StringComparison.Ordinal),
        methodSig[..sigSplitIndex].ToString().Replace("System.__Canon", "System.Object", StringComparison.Ordinal),
        methodSig[(sigSplitIndex + 1)..].ToString().Replace("System.__Canon", "System.Object", StringComparison.Ordinal));
}

string[] ParseMethodTypeArgs(ReadOnlySpan<char> methodName)
{
    var args = new List<string>();
    int i = 0;
    int inTypeArg = 0;
    var chunk = new StringBuilder();

    while (i < methodName.Length)
    {
        switch (methodName[i])
        {
            case '[':
                if (inTypeArg > 0)
                {
                    chunk.Append(methodName[i]);
                }
                inTypeArg++;
                break;
            case ']':
                inTypeArg--;
                if (inTypeArg == 0)
                {
                    args.Add(chunk.ToString().Replace("System.__Canon", "System.Object", StringComparison.Ordinal)[1..^1]);
                    chunk.Clear();
                }
                else
                {
                    chunk.Append(methodName[i]);
                }
                break;
            case ',':
                if (inTypeArg == 1)
                {
                    args.Add(chunk.ToString().Replace("System.__Canon", "System.Object", StringComparison.Ordinal)[1..^1]);
                    chunk.Clear();
                }
                else
                {
                    chunk.Append(methodName[i]);
                }
                break;
            default:
                if (inTypeArg > 0)
                {
                    chunk.Append(methodName[i]);
                }
                break;
        }

        i++;
    }

    return args.ToArray();
}

bool FilterTypeAndMethod(ClrMethod methodInfo, ReadOnlySpan<char> typeName, ReadOnlySpan<char> methodName)
{
    // filter dynamic stubs
    if (methodInfo.Type.Name?.StartsWith("(dynamicClass)") ?? true)
    {
        return false;
    }

    // filter async helpers
    if (typeName.StartsWith("System.Runtime.CompilerServices")
        || typeName.StartsWith("System.Runtime.CompilerServices.AsyncMethodBuilderCore")
        || typeName.StartsWith("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder")
        || typeName.StartsWith("System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder")
        || typeName.StartsWith("System.Runtime.CompilerServices.ValueTaskAwaiter"))
    {
        return false;
    }

    // filter private helpers
    if (typeName.StartsWith("System.Runtime.CompilerServices.RuntimeHelpers")
        && methodInfo.Attributes.HasFlag(MethodAttributes.Private))
    {
        return false;
    }

    // filter coreclr impl details
    if (typeName.StartsWith("System.Collections.Generic.ArraySortHelper")
        || typeName.StartsWith("System.Collections.Generic.GenericArraySortHelper")
        || typeName.StartsWith("System.SZArrayHelper")
        || (typeName.StartsWith("System.Linq") && methodInfo.Attributes.HasFlag(MethodAttributes.Private))
        || (typeName.StartsWith("System.Collections") && typeName.IndexOf("+Enumerator") != -1)
        || typeName.StartsWith("System.Collections.Immutable.SecureObjectPool"))
    {
        return false;
    }

    // any others?

    return true;
}

string EscapeXmlAttribute(string str)
{
    return str
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}

var t = new Thread(() =>
{
    try
    {
        source.Process();
    }
    catch
    {
        // ignored
    }
});

t.Start();

var inputTask = Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream);
var outputTask = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
var errorTask = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;

    try
    {
        source.StopProcessing();
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
    }
    catch
    {
        // ignored
    }

    try
    {
        process.Kill();
    }
    catch
    {
        // ignored
    }
};

try
{
    await Task.WhenAll(inputTask, outputTask, errorTask);
}
catch
{
    // ignored
}

t.Join();

if (result.Count != 0)
{
    Console.WriteLine("<Directives>");
    Console.WriteLine("    <Application>");

    foreach (var (assembly, types) in result)
    {
        Console.WriteLine($"        <Assembly Name=\"{EscapeXmlAttribute(assembly)}\">");
        foreach (var (type, methods) in types)
        {
            Console.WriteLine($"            <Type Name=\"{EscapeXmlAttribute(type)}\">");
            foreach (var method in methods)
            {
                var splitIndex = method.IndexOf('[');
                if (splitIndex < 0) splitIndex = method.Length;
                var typeArgs = ParseMethodTypeArgs(method);
                if (typeArgs.Length > 0)
                {
                    Console.WriteLine($"                <Method Name=\"{EscapeXmlAttribute(method[..splitIndex])}\">");
                    foreach (var arg in typeArgs)
                    {
                        Console.WriteLine($"                    <GenericArgument Name=\"{EscapeXmlAttribute(arg)}\" />");
                    }
                    Console.WriteLine($"                </Method>");
                }
                else
                {
                    Console.WriteLine($"                <Method Name=\"{EscapeXmlAttribute(method[..splitIndex])}\" />");
                }
            }
            Console.WriteLine($"            </Type>");
        }
        Console.WriteLine($"        </Assembly>");
    }

    Console.WriteLine("    </Application>");
    Console.WriteLine("</Directives>");
}

return 0;