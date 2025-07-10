using System;
using System.Reflection;
using UnityEngine;

public static class Logs
{
    private static string modName = Assembly.GetExecutingAssembly().GetName().Name;

    public static void Write(object str, LogType logType = LogType.Log)
    {
        string message = "<color=lime>[" + modName + "]</color>: " + str;
        if (ModConfig.isDebug)
        {
            switch (logType)
            {
                case LogType.Warning:
                    Log.Warning(message);
                    break;
                case LogType.Error:
                    Log.Error(message);
                    break;
                default:
                    Log.Out(message);
                    break;
            }
        }
    }
    public static void Error(object str, Exception ex)
    {
        string message = "<color=lime>[" + modName + "]</color>: " + str;
        Log.Error(message);
        Log.Exception(ex);
    }

}
