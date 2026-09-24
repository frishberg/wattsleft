"""Before uploading: open every built package and check that each assembly WattsLeft.deps.json lists is really in it.

1.1.0 went to the Store with a deps.json that named a DLL the package didn't list (a stale obj/ from the other build),
and it crashed on launch for everyone. Neither the certification kit nor Store certification caught it. This does.

  python check-packages.py 1.2.0.0    (from this folder, after building the Store packages and the .exe; the version picks the packages)
Exit code 0 only if every package passes.
"""
import glob, json, os, sys, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
REQUIRED = ["WattsLeft.exe", "WattsLeft.dll", "Microsoft.WinUI.dll", "WinRT.Runtime.dll", "Microsoft.Windows.SDK.NET.dll"]
# The Store package starts the Windows App SDK's deployment manager, which loads this one; the .exe build doesn't use it.
REQUIRED_STORE = REQUIRED + ["Microsoft.Windows.ApplicationModel.WindowsAppRuntime.Projection.dll"]


def listed(deps):
    """Every runtime file the deps.json promises, by file name."""
    names = set()
    for target in deps.get("targets", {}).values():
        for lib in target.values():
            for section in ("runtime", "native"):
                for path in lib.get(section, {}):
                    names.add(os.path.basename(path))
    return names


def check(label, files, read, required):
    problems = []
    if "WattsLeft.deps.json" not in files:
        return [f"{label}: no WattsLeft.deps.json"]
    deps = json.loads(read("WattsLeft.deps.json"))
    have = {os.path.basename(f) for f in files}
    for name in sorted(listed(deps)):
        if name not in have:
            problems.append(f"{label}: deps.json lists {name}, which isn't in the package")
    for name in required:
        if name not in have:
            problems.append(f"{label}: {name} is missing")
        elif name.endswith(".dll") and name not in listed(deps) and name != "WattsLeft.dll":
            problems.append(f"{label}: {name} is in the package but deps.json doesn't list it, so .NET won't load it")
    return problems


problems, checked = [], 0
version = sys.argv[1] if len(sys.argv) > 1 else "*"
for msix in sorted(glob.glob(os.path.join(ROOT, "AppPackages", f"*_{version}_*_Test", "*.msix"))):
    with zipfile.ZipFile(msix) as z:
        names = z.namelist()
        problems += check(os.path.basename(msix), names, lambda n: z.read(n).decode("utf-8-sig"), REQUIRED_STORE)
    checked += 1
publish = os.path.join(ROOT, "bin", "unpackaged", "win-x64")
if os.path.isdir(publish):
    names = os.listdir(publish)
    problems += check("exe build (bin/unpackaged/win-x64)", names, lambda n: open(os.path.join(publish, n), encoding="utf-8-sig").read(), REQUIRED)
    checked += 1

for p in problems:
    print("FAIL", p)
print(f"{checked} package(s) checked, {len(problems)} problem(s)")
sys.exit(1 if problems or checked == 0 else 0)
