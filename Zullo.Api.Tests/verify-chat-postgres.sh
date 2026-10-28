#!/usr/bin/env bash
set -euo pipefail
repository_root="$(cd -- "$(dirname -- "$0")/.." && pwd)"
task_temp_dir="$(mktemp -d)"
trap 'rm -rf -- "$task_temp_dir"' EXIT
cp "$repository_root/Zullo.Api.Tests/ChatLoadCheck.csx" "$task_temp_dir/Program.cs"
cat > "$task_temp_dir/ChatCheck.csproj" <<PROJECT
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="8.0.23" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" Version="8.0.24" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="8.0.24" />
    <ProjectReference Include="$repository_root/Zullo.Api.csproj" />
  </ItemGroup>
</Project>
PROJECT
dotnet run --project "$task_temp_dir/ChatCheck.csproj"
