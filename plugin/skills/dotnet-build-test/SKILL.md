---
name: dotnet-build-test
description: How to build, test and diagnose failures in .NET solutions. Use when building or running tests in a C#/.NET repository.
---

# Building and testing .NET

1. Find the entry point: prefer a `*.slnx`/`*.sln` at or above the working directory; otherwise the nearest `*.csproj`.
2. Build quietly and surface only problems:
   `dotnet build <sln> -v q 2>&1 | grep -E "error|warning" | sort -u`
3. Run tests: `dotnet test <sln> --no-build`. To run a subset: `--filter "FullyQualifiedName~<Name>"`.
4. On failure, read the first error before the rest — later errors are often cascades.
5. Package restore problems: `dotnet restore <sln>` and check `global.json` / SDK version with `dotnet --list-sdks`.

Do not add `TreatWarningsAsErrors` or change target frameworks without asking.
