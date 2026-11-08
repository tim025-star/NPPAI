using System.Reflection;
using System.Text.Json;

namespace NPPAI.Setup;

internal static class Program
{
    internal static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidDataException("Missing setup resource: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [STAThread]
    static void Main(string[] args)
    {
        // This read-only probe also verifies that the published single EXE can load its resources.
        var package = JsonSerializer.Deserialize<Package>(Resource("release-assets.json"), SetupCore.Json)!;
        SetupCore.Parts(package);
        if (args.Length == 2 && args[0] == "--verify-setup")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { package.Tag, Parts = SetupCore.Parts(package).Length, Licences = Resource("Model-Licenses.txt").Length }));
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm(package));
    }
}
