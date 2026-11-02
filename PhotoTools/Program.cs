using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Zullo.Api.Data;
using Zullo.Api.Services;

// Plan first. Writes require an explicit digest of the reviewed, locally stored plan.
var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var connection = DatabaseConnection.Resolve(config);
var expected = config["PhotosAdmin:ExpectedDatabase"];
if (string.IsNullOrWhiteSpace(expected) || new NpgsqlConnectionStringBuilder(connection).Database != expected)
    throw new InvalidOperationException("Set PhotosAdmin__ExpectedDatabase to the database being reviewed.");
await using var dataSource = DatabaseConnection.CreateDataSource(config);
await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(dataSource).Options);
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(50) };
var provider = new CloudinaryPhotoProvider(http, config);
var lifecycle = new PhotoLifecycle(db, provider);
var adoption = new LegacyPhotoAdoption(db, lifecycle, provider);
if (args.Length == 2 && args[0] == "--plan") {
    var plan = await adoption.Plan(50);
    var text = LegacyPhotoAdoption.Serialize(plan);
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    await using var file = new FileStream(args[1], options);
    await using var writer = new StreamWriter(file);
    await writer.WriteAsync(text);
    Console.WriteLine($"Review file created: {plan.Entries.Count} candidate assets; {plan.ReviewRequired} require separate review.");
    Console.WriteLine($"SHA256: {LegacyPhotoAdoption.Digest(text)}");
} else if (args.Length == 4 && args[0] == "--apply" && args[2] == "--approve-sha256") {
    var text = await File.ReadAllTextAsync(args[1]);
    var plan = System.Text.Json.JsonSerializer.Deserialize<LegacyPhotoPlan>(text) ?? throw new InvalidOperationException("Invalid plan.");
    if (args[3] != LegacyPhotoAdoption.Digest(text)) throw new InvalidOperationException("File changed after review.");
    await adoption.Apply(plan, args[3]);
    Console.WriteLine("Reviewed legacy ownership mappings applied. Closed/unlinked assets are queued for verified cleanup.");
} else {
    throw new InvalidOperationException("Use --plan file.json, then review ownership and use --apply file.json --approve-sha256 DIGEST. No writes without explicit approval.");
}
