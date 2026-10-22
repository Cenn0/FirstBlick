using System.Text.Json;
using FirstBlick.TechnicalFixtures;

if (args.Length != 1) throw new ArgumentException("Supply the explicit output JSON path.");
File.WriteAllText(args[0], JsonSerializer.Serialize(Fixtures.Create(), Fixtures.JsonOptions) + "\n");
