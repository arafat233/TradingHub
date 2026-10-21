using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TradingHub.Scripting;

/// <summary>A compilation error or warning.</summary>
public sealed record ScriptDiagnostic(string File, int Line, int Column, string Severity, string Id, string Message)
{
    public override string ToString() => $"{Path.GetFileName(File)}({Line},{Column}): {Severity} {Id}: {Message}";
}

public sealed class ScriptCompileResult
{
    public bool Success { get; init; }
    public Assembly? Assembly { get; init; }
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; init; } = Array.Empty<ScriptDiagnostic>();
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Compiles C# indicators at run time (Roslyn), just as ATAS compiles its own.
/// It accepts code written for ATAS: it translates its namespaces (ATAS.Indicators, OFT.Rendering...)
/// to Trading Hub's, which have the same API.
/// </summary>
public sealed class ScriptCompiler
{
    private static readonly (Regex Pattern, string Replacement)[] NamespaceMap =
    {
        (new Regex(@"\bATAS\.Indicators\.Technical\b"), "TradingHub.Indicators.Technical"),
        (new Regex(@"\bATAS\.Indicators\.Drawing\b"), "TradingHub.Indicators.Drawing"),
        (new Regex(@"\bATAS\.Indicators\b"), "TradingHub.Indicators"),
        (new Regex(@"\bATAS\.DataFeedsCore\b"), "TradingHub.Trading"),
        (new Regex(@"\bOFT\.Localization\b"), "TradingHub.Localization"),
        (new Regex(@"\bOFT\.Attributes\.Editors\b"), "TradingHub.Attributes.Editors"),
        (new Regex(@"\bOFT\.Attributes\b"), "TradingHub.Attributes"),
        (new Regex(@"\bOFT\.Rendering\.Context\.GDIPlus\b"), "TradingHub.Rendering.Context"),
        (new Regex(@"\bOFT\.Rendering\.Settings\b"), "TradingHub.Rendering.Settings"),
        (new Regex(@"\bOFT\.Rendering\.Tools\b"), "TradingHub.Rendering.Tools"),
        (new Regex(@"\bOFT\.Rendering\.Control\b"), "TradingHub.Rendering.Control"),
        (new Regex(@"\bOFT\.Rendering\.Context\b"), "TradingHub.Rendering.Context"),
        (new Regex(@"\bOFT\.Rendering\b"), "TradingHub.Rendering"),
        (new Regex(@"\bUtils\.Common\.Logging\b"), "TradingHub.Common.Logging"),
        (new Regex(@"\bUtils\.Common\.Collections\b"), "TradingHub.Common.Collections"),
        (new Regex(@"\bUtils\.Common\b"), "TradingHub.Common"),
    };

    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using CrossColor = System.Drawing.Color;
        global using CrossColors = System.Drawing.Color;
        global using CrossKey = Avalonia.Input.Key;
        global using CrossKeyEventArgs = Avalonia.Input.KeyEventArgs;
        global using CrossSolidBrush = TradingHub.Common.UniversalSolidBrush;
        global using CrossPen = TradingHub.Common.UniversalPen;
        global using CrossPens = TradingHub.Common.UniversalPens;
        """;

    /// <summary>Adapts code written for ATAS to Trading Hub's namespaces.</summary>
    public static string Preprocess(string source)
    {
        foreach (var (p, r) in NamespaceMap) source = p.Replace(source, r);
        return source;
    }

    private static List<MetadataReference> BuildReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (path.Length > 0 && File.Exists(path) && seen.Add(Path.GetFileName(path)))
                    refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
            if (seen.Add(Path.GetFileName(asm.Location))) refs.Add(MetadataReference.CreateFromFile(asm.Location));
        }

        return refs;
    }

    /// <summary>Compiles .cs files into an assembly loaded in memory (unloadable).</summary>
    public ScriptCompileResult Compile(IReadOnlyList<string> files, string assemblyName)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var parse = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: new[] { "CROSS_PLATFORM", "TRADINGHUB" });
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(GlobalUsings, parse, path: "GlobalUsings.g.cs", encoding: System.Text.Encoding.UTF8) };
        foreach (var f in files)
        {
            string source;
            try
            {
                source = File.ReadAllText(f);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue; // it was deleted between listing the folder and reading it: it is left out (whoever watches the folder compiles again)
            }

            var text = Preprocess(source);
            trees.Add(CSharpSyntaxTree.ParseText(text, parse, path: f, encoding: System.Text.Encoding.UTF8));
        }

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            nullableContextOptions: NullableContextOptions.Annotations,
            allowUnsafe: true);
        var compilation = CSharpCompilation.Create(assemblyName + "_" + Guid.NewGuid().ToString("N")[..8], trees, BuildReferences(), options);

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emit = compilation.Emit(pe, pdb);
        var diags = emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error || (d.Severity == DiagnosticSeverity.Warning && !IgnoredWarnings.Contains(d.Id)))
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return new ScriptDiagnostic(span.Path ?? "", span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                    d.Severity == DiagnosticSeverity.Error ? "error" : "warning", d.Id, d.GetMessage(System.Globalization.CultureInfo.GetCultureInfo("en")));
            })
            .ToList();

        if (!emit.Success)
            return new ScriptCompileResult { Success = false, Diagnostics = diags, Elapsed = sw.Elapsed };

        pe.Position = 0;
        pdb.Position = 0;
        var alc = new AssemblyLoadContext(assemblyName, isCollectible: true);
        var asm = alc.LoadFromStream(pe, pdb);
        return new ScriptCompileResult { Success = true, Assembly = asm, Diagnostics = diags, Elapsed = sw.Elapsed };
    }

    private static readonly HashSet<string> IgnoredWarnings = new()
    {
        "CS0168", "CS0219", "CS0414", "CS0649", "CS0169", "CS0067", "CS0618", "CS0612", "CS8632", "CS1998", "CS4014", "CS0162", "CS0108", "CS0114",
    };
}
