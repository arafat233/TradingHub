#!/usr/bin/env python3
"""
Ports the public ATAS indicators (official AtasPlatform/Indicators repo, Technical folder)
to the Trading Hub API. It only adapts the namespaces: the code stays the same.

Usage:
    python3 tools/port_atas.py [path_to_ATAS_repo] [destination_folder]

By default: third_party/ATAS-Indicators -> src/TradingHub.Indicators.Technical/Atas

To download the repository the first time:
    git clone https://github.com/AtasPlatform/Indicators.git third_party/ATAS-Indicators

To update it when ATAS publishes changes:
    git -C third_party/ATAS-Indicators pull && python3 tools/port_atas.py
"""
import os
import re
import shutil
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "third_party", "ATAS-Indicators")
DST = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "src", "TradingHub.Indicators.Technical", "Atas")

# Folders/files that depend on ATAS internals (their own WPF/Avalonia editors,
# heatmap and DOM v3, which use internal classes of their platform).
EXCLUDE_DIRS = {"Editors", "Properties", "obj", "bin", "Heatmap", "DomV3"}
EXCLUDE_SUFFIXES = (".Windows.cs", ".xaml.cs")
# TRADINGHUB_PORT_EXCLUDE=file1.cs,file2.cs leaves out the ones you don't want to port.
EXCLUDE_FILES = set(os.environ.get("TRADINGHUB_PORT_EXCLUDE", "").split(",")) - {""}

# Order matters: the longest namespaces first.
MAP = [
    (r"\bATAS\.Indicators\.Technical\b", "TradingHub.Indicators.Technical"),
    (r"\bATAS\.Indicators\.Drawing\b", "TradingHub.Indicators.Drawing"),
    (r"\bATAS\.Indicators\b", "TradingHub.Indicators"),
    (r"\bATAS\.DataFeedsCore\b", "TradingHub.Trading"),
    (r"\bOFT\.Localization\b", "TradingHub.Localization"),
    (r"\bOFT\.Attributes\.Editors\b", "TradingHub.Attributes.Editors"),
    (r"\bOFT\.Attributes\b", "TradingHub.Attributes"),
    (r"\bOFT\.Rendering\.Context\.GDIPlus\b", "TradingHub.Rendering.Context"),
    (r"\bOFT\.Rendering\.Settings\b", "TradingHub.Rendering.Settings"),
    (r"\bOFT\.Rendering\.Tools\b", "TradingHub.Rendering.Tools"),
    (r"\bOFT\.Rendering\.Control\b", "TradingHub.Rendering.Control"),
    (r"\bOFT\.Rendering\.Context\b", "TradingHub.Rendering.Context"),
    (r"\bOFT\.Rendering\b", "TradingHub.Rendering"),
    (r"\bUtils\.Common\.Logging\b", "TradingHub.Common.Logging"),
    (r"\bUtils\.Common\.Collections\b", "TradingHub.Common.Collections"),
    (r"\bUtils\.Common\b", "TradingHub.Common"),
]

HEADER = (
    "// Ported automatically from the public ATAS repository (github.com/AtasPlatform/Indicators)\n"
    "// by tools/port_atas.py. Personal use: the code belongs to ATAS, respect their license.\n"
)


# Fixes applied to the already ported ATAS code (they apply if the original did not change; if it did, it warns).
# Valuation is a class in TradingHub.Trading, not a namespace: its "using" does not compile.
PATCHES = [
    ("Volume.cs", "using TradingHub.Trading.Valuation;\n", ""),
    ("OpenInterest.cs", "\tusing TradingHub.Trading.Valuation;\n", ""),
    ("OrderFlow.cs", "\tusing TradingHub.Trading.Valuation;\n", ""),
    # with no volatility (ATR = 0) there is no directional movement to measure: without this it divides by zero
    ("DmIndex.cs",
     "\t\t\t_upSeries[bar] = smoothedUp / _atr[bar] * 100m;\n\t\t\t_downSeries[bar] = smoothedDown / _atr[bar] * 100m;\n",
     "\t\t\t// with no volatility (ATR = 0) there is no directional movement to measure\n"
     "\t\t\t_upSeries[bar] = _atr[bar] == 0 ? 0 : smoothedUp / _atr[bar] * 100m;\n"
     "\t\t\t_downSeries[bar] = _atr[bar] == 0 ? 0 : smoothedDown / _atr[bar] * 100m;\n"),
    # the grey rectangles with Trading Hub's own brush (Brushes.Gray does not draw properly here)
    ("MarginZones.cs", "CrossPens.Transparent, Brushes.Gray)", "CrossPens.Transparent, new TradingHub.Common.UniversalSolidBrush(System.Drawing.Color.Gray))"),
    # DynamicLevelsChannel sums each price's volume by walking the whole list again and again: a dictionary
    # built once gives the same result, much faster
    ("DynamicLevelsChannel.cs", "            var low = _volumeGroup.Min(x => x.Price);\n",
     "            var low = _volumeGroup.Min(x => x.Price);\n"
     "            var volumeAt = new Dictionary<decimal, decimal>();\n"
     "            foreach (var g in _volumeGroup)\n"
     "                volumeAt[g.Price] = volumeAt.TryGetValue(g.Price, out var acc) ? acc + g.Volume : g.Volume;\n"),
    ("DynamicLevelsChannel.cs", "                var vol = _volumeGroup\n                    .Where(x => x.Price == _maxPrice)\n                    .Sum(x => x.Volume);\n",
     "                var vol = volumeAt.GetValueOrDefault(_maxPrice);\n"),
    ("DynamicLevelsChannel.cs", "                            upperVol += _volumeGroup\n                                .Where(x => x.Price == upperPrice)\n                                .Sum(x => x.Volume);\n",
     "                            upperVol += volumeAt.GetValueOrDefault(upperPrice);\n"),
    ("DynamicLevelsChannel.cs", "                        lowerVol += _volumeGroup\n                            .Where(x => x.Price == lowerPrice)\n                            .Sum(x => x.Volume);\n",
     "                        lowerVol += volumeAt.GetValueOrDefault(lowerPrice);\n"),
]


def port_text(text: str) -> str:
    for pat, rep in MAP:
        text = re.sub(pat, rep, text)
    return text


def patch(name: str, text: str) -> str:
    for file, old, new in PATCHES:
        if file != name:
            continue
        if old in text:
            text = text.replace(old, new)
        else:
            print(f"warning: a fix for {name} was not applied (the ATAS original changed)")
    return text


def main():
    tech = os.path.join(SRC, "Technical")
    if not os.path.isdir(tech):
        sys.exit(f"Cannot find {tech}")
    if os.path.isdir(DST):
        shutil.rmtree(DST)
    count = 0
    for dirpath, dirnames, filenames in os.walk(tech):
        dirnames[:] = [d for d in dirnames if d not in EXCLUDE_DIRS]
        for fn in filenames:
            if not fn.endswith(".cs") or fn.endswith(EXCLUDE_SUFFIXES):
                continue
            rel = os.path.relpath(os.path.join(dirpath, fn), tech)
            if rel in EXCLUDE_FILES or fn in EXCLUDE_FILES:
                continue
            with open(os.path.join(dirpath, fn), encoding="utf-8-sig") as f:
                text = f.read()
            out = os.path.join(DST, rel)
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(out, "w", encoding="utf-8") as f:
                f.write(HEADER + patch(fn, port_text(text)))
            count += 1
    print(f"Ported {count} files to {DST}")


if __name__ == "__main__":
    main()
