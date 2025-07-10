using Newtonsoft.Json;
using System;
using System.IO;
using System.Reflection;

public static class ModConfig
{
    private static readonly string modName = Assembly.GetExecutingAssembly().GetName().Name;

    public static bool isDebug = false;

    private static ConfigClass config;

    public static void Load()
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), modName + ".json");
            if (File.Exists(path))
            {
                config = JsonConvert.DeserializeObject<ConfigClass>(File.ReadAllText(path));
                isDebug = config.isDebug;
            }
            else
            {
                config = new ConfigClass();
            }
            File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
            string message = "<color=lime>[" + modName + "]</color>: Config loaded";
            Log.Out(message);
        }
        catch (Exception ex)
        {
            string message = "<color=lime>[" + modName + "]</color>: Config failed to load";
            Log.Error(message);
            Log.Exception(ex);
        }
    }

}

public class ConfigClass
{
    public bool isDebug = false;
}


