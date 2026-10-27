#!/usr/bin/env bash
set -euo pipefail
repository_root="$(cd -- "$(dirname -- "$0")/.." && pwd)"
task_temp_dir="$(mktemp -d)"
trap 'rm -rf -- "$task_temp_dir"' EXIT
cp "$repository_root/Zullo.Api.Tests/ProfileMigrationCheck.csx" "$task_temp_dir/Program.cs"
cat > "$task_temp_dir/MigrationCheck.csproj" <<PROJECT
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="8.0.23" />
    <ProjectReference Include="$repository_root/Zullo.Api.csproj" />
  </ItemGroup>
</Project>
PROJECT
dotnet run --project "$task_temp_dir/MigrationCheck.csproj"
