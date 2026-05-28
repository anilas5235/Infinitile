using UnityEngine;

namespace Engine.Scripts.Utils
{
    /// <summary>
    ///     <para>
    ///         Base class for MonoBehaviour singletons. Only one instance will exist at runtime.
    ///         There is no instance creation logic, so you must ensure the singleton is created in the scene.
    ///     </para>
    /// </summary>
    /// <remarks>Author: Niklas Borchers. Use <see cref="Instance" /> to access the active object.</remarks>
    [DefaultExecutionOrder(-100)]
    public abstract class Singleton<T> : MonoBehaviour where T : MonoBehaviour
    {
        /// <summary>
        ///     Gets the singleton instance of this type. Null until the first <see cref="Awake" /> is executed.
        /// </summary>
        public static T Instance { get; private set; }

        /// <summary>
        ///     Ensures only one instance exists and assigns the static <see cref="Instance" /> property.
        ///     Destroys duplicates automatically.
        /// </summary>
        protected virtual void Awake()
        {
            if (!Instance) Instance = gameObject.GetComponent<T>();
            else if (Instance.GetInstanceID() != GetInstanceID()) Destroy(this);
        }

        /// <summary>
        ///     Clears the static <see cref="Instance" /> property if this instance is destroyed.
        /// </summary>
        protected virtual void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}