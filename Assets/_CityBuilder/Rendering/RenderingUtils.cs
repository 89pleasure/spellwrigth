using UnityEngine;

#nullable enable
namespace CityBuilder.Rendering
{
    /// <summary>
    /// Shared rendering utilities. Caches expensive resources that would
    /// otherwise be re-created per call (e.g. fallback materials).
    /// </summary>
    public static class RenderingUtils
    {
        private static Material? _defaultErrorMaterial;

        /// <summary>
        /// A single cached error material for use as a last-resort fallback
        /// when no materials are assigned in the Inspector.
        /// </summary>
        public static Material DefaultErrorMaterial
        {
            get
            {
                if (!_defaultErrorMaterial)
                {
                    _defaultErrorMaterial = new Material(Shader.Find("Hidden/InternalErrorShader")!);
                }

                return _defaultErrorMaterial;
            }
        }
    }
}
