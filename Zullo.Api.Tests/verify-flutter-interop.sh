#!/usr/bin/env bash
set -euo pipefail
umask 077
repository_root="$(cd -- "$(dirname -- "$0")/.." && pwd)"
frontend_root="${1:?Usage: verify-flutter-interop.sh /path/to/zullo-frontend}"
frontend_root="$(cd -- "$frontend_root" && pwd)"
test -f "$frontend_root/test/integration_backend_interop.dart"
task_temp_dir="$(mktemp -d)"
trap 'rm -rf -- "$task_temp_dir"' EXIT
cp "$repository_root/Zullo.Api.Tests/FlutterInteropCheck.csx" "$task_temp_dir/Program.cs"
cat > "$task_temp_dir/Interop.csproj" <<PROJECT
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="8.0.23" />
    <ProjectReference Include="$repository_root/Zullo.Api.csproj" />
  </ItemGroup>
</Project>
PROJECT
dotnet run --project "$task_temp_dir/Interop.csproj" -- "$frontend_root" "$task_temp_dir/fixture.json"
