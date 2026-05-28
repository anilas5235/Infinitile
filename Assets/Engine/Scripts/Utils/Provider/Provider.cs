using System;

namespace Engine.Scripts.Utils.Provider
{
    /// <summary>
    ///     Generic provider base class for lazy or explicit initialization of a singleton-like instance.
    ///     Uses a static <see cref="Current" /> reference and allows external setup via callbacks.
    /// </summary>
    /// <typeparam name="TP">Concrete provider type (self-referential generic).</typeparam>
    public abstract class Provider<TP> where TP : Provider<TP>, new()
    {
        /// <summary>
        ///     Gets the currently active provider instance.
        /// </summary>
        public static TP Current { get; private set; }

        /// <summary>
        ///     Initializes the provider with an already created instance and invokes the initializer callback.
        /// </summary>
        /// <param name="provider">Existing provider instance to register as current.</param>
        /// <param name="initializer">Callback used to perform additional initialization on the provider.</param>
        public static void Initialize(TP provider, Action<TP> initializer)
        {
            Current = provider;
            initializer(Current);
        }

        /// <summary>
        ///     Creates a new provider instance using the <c>new()</c> constraint and invokes the initializer callback.
        /// </summary>
        /// <param name="initializer">Callback used to perform additional initialization on the provider.</param>
        public static void Initialize(Action<TP> initializer)
        {
            Current = new TP();
            initializer(Current);
        }
    }
}