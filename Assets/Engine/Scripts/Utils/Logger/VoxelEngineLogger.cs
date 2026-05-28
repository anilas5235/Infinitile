using UnityEngine;

namespace Engine.Scripts.Utils.Logger
{
    /// <summary>
    ///     Colored logger wrapper around the Unity logger that automatically generates tags per type or name
    ///     and supports log level configuration and custom handlers.
    /// </summary>
    public static class VoxelEngineLogger
    {
        private static readonly string LOGTag =
            $"<color=#{ColorUtility.ToHtmlStringRGB(GetColor("Voxel"))}>[Voxel]</color> ";

        /// <summary>
        ///     Creates a tag with a unique color derived from the given type.
        /// </summary>
        /// <typeparam name="T">Type for which the tag should be created.</typeparam>
        /// <returns>Color-formatted tag string.</returns>
        public static string GetTag<T>()
        {
            return GetTag(typeof(T).Name.Split('`')[0]);
        }

        /// <summary>
        ///     Creates a tag with a unique color derived from the given name.
        /// </summary>
        /// <param name="name">Raw tag name.</param>
        /// <returns>Color-formatted tag string.</returns>
        public static string GetTag(string name)
        {
            return $"{LOGTag}<color=#{ColorUtility.ToHtmlStringRGB(GetColor(name))}>{name}</color>";
        }

        /// <summary>
        ///     Creates a tag with the specified color for the given type.
        /// </summary>
        /// <param name="color">Hex code or name of the color (must be a valid Unity color name).</param>
        /// <typeparam name="T">Type for which the tag should be created.</typeparam>
        /// <returns>Color-formatted tag string.</returns>
        public static string GetTag<T>(string color)
        {
            return GetTag(typeof(T).Name.Split('`')[0], color);
        }

        /// <summary>
        ///     Creates a tag with the specified color for the given name.
        /// </summary>
        /// <param name="name">Tag value.</param>
        /// <param name="color">Hex code or name of the color (must be a valid Unity color name).</param>
        /// <returns>Color-formatted tag string.</returns>
        public static string GetTag(string name, string color)
        {
            return $"<color={color}>{name}</color>";
        }

        /// <summary>
        ///     Sets the Unity logger log level (filter).
        /// </summary>
        /// <param name="level">Minimum log type that should be processed.</param>
        public static void SetLogLevel(LogType level)
        {
            Debug.unityLogger.filterLogType = level;
        }

        /// <summary>
        ///     Enables or disables logging globally.
        /// </summary>
        /// <param name="enable">If <c>true</c>, logging is enabled; otherwise it is disabled.</param>
        public static void EnableLogging(bool enable)
        {
            Debug.unityLogger.logEnabled = enable;
        }

        /// <summary>
        ///     Sets a custom log handler on the Unity logger.
        /// </summary>
        /// <param name="handler">Log handler instance to forward all log calls to.</param>
        public static void SetLogHandler(ILogHandler handler)
        {
            Debug.unityLogger.logHandler = handler;
        }

        /// <summary>
        ///     Logs an informational message with a colored tag derived from the generic type.
        /// </summary>
        /// <typeparam name="T">Type whose name is used as the log tag.</typeparam>
        /// <param name="message">Message to log.</param>
        public static void Info<T>(string message)
        {
            Debug.unityLogger.Log(LogType.Log, GetTag<T>(), message);
        }

        /// <summary>
        ///     Logs a warning message with a colored tag derived from the generic type.
        /// </summary>
        /// <typeparam name="T">Type whose name is used as the log tag.</typeparam>
        /// <param name="message">Message to log.</param>
        public static void Warn<T>(string message)
        {
            Debug.unityLogger.Log(LogType.Warning, GetTag<T>(), message);
        }

        /// <summary>
        ///     Logs an error message with a colored tag derived from the generic type.
        /// </summary>
        /// <typeparam name="T">Type whose name is used as the log tag.</typeparam>
        /// <param name="message">Message to log.</param>
        public static void Error<T>(string message)
        {
            Debug.unityLogger.Log(LogType.Error, GetTag<T>(), message);
        }

        /// <summary>
        ///     Logs an informational message with a custom raw tag name.
        /// </summary>
        /// <param name="tag">Raw tag name.</param>
        /// <param name="message">Message to log.</param>
        public static void Info(string tag, string message)
        {
            Debug.unityLogger.Log(LogType.Log, GetTag(tag), message);
        }

        /// <summary>
        ///     Logs a warning message with a custom raw tag name.
        /// </summary>
        /// <param name="tag">Raw tag name.</param>
        /// <param name="message">Message to log.</param>
        public static void Warn(string tag, string message)
        {
            Debug.unityLogger.Log(LogType.Warning, GetTag(tag), message);
        }

        /// <summary>
        ///     Logs an error message with a custom raw tag name.
        /// </summary>
        /// <param name="tag">Raw tag name.</param>
        /// <param name="message">Message to log.</param>
        public static void Error(string tag, string message)
        {
            Debug.unityLogger.Log(LogType.Error, GetTag(tag), message);
        }

        /// <summary>
        ///     Normalizes the hue value to avoid unreadable colors in the 0.6 to 0.7 range.
        /// </summary>
        /// <param name="hue">Original hue value.</param>
        /// <returns>Adjusted hue value that avoids problematic color ranges.</returns>
        private static float NormalizeHue(float hue)
        {
            return Mathf.Lerp(0.7f, 1.6f, hue) % 1;
        }

        private static Color GetColor(string name)
        {
            float hue = ((float)name.GetHashCode() % 10000 / 10000 + 1) / 2;
            return Color.HSVToRGB(NormalizeHue(hue), 1f, 1f);
        }
    }
}