using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;

namespace CityBuilder.Infrastructure.Roads
{
    /// <summary>
    /// Describes one lane-width strip in the road cross-section.
    /// Strips are ordered left to right in the profile.
    /// </summary>
    [Serializable]
    public struct ProfileStrip
    {
        [Tooltip("Width of this strip in metres.")]
        public float width;

        [Tooltip("Height of this strip's surface above the road baseline (e.g. 0.15 m for a kerb).")]
        public float heightOffset;

        [FormerlySerializedAs("Type")] [Tooltip("What this strip represents – used by the mesh builder and the renderer.")]
        public StripType type;

        [Tooltip("Which material slot to use in the MeshRenderer. All strips sharing an index use the same material.")]
        public int materialIndex;
    }

    /// <summary>
    /// Functional category of a profile strip.
    /// Drives visual variation (textures) and simulation logic (e.g. pedestrians
    /// only walk on Pavement; vehicles only drive on Carriageway).
    /// </summary>
    public enum StripType
    {
        Carriageway,    // Vehicle driving surface
        Pavement,       // Pedestrian footpath
        CycleWay,       // Bicycle lane
        Median,         // Central reservation between opposing traffic directions
        Kerb,           // Raised edge between carriageway and pavement
        Shoulder,       // Unpaved hard shoulder
    }

    /// <summary>
    /// Defines the full cross-section of a road type as an ordered list of strips.
    ///
    /// This is the main extensibility point: adding a new road type (cycle lane,
    /// motorway, alley) only requires creating a new RoadProfile asset in the
    /// Unity Editor – no code changes needed.
    ///
    /// Example – standard 2-lane urban road (11 m total):
    ///   [Pavement 2 m] [Carriageway 3.5 m] [Carriageway 3.5 m] [Pavement 2 m]
    ///
    /// Example – arterial road with cycle lanes (15 m total):
    ///   [Pavement 2 m] [CycleWay 1.5 m] [Carriageway 3.5 m] [Carriageway 3.5 m] [CycleWay 1.5 m] [Pavement 2 m]
    /// </summary>
    [CreateAssetMenu(fileName = "RoadProfile", menuName = "CityBuilder/Road Profile")]
    public class RoadProfile : ScriptableObject
    {
        [Tooltip("Cross-section strips ordered from left edge to right edge of the road.")]
        public ProfileStrip[] strips = Array.Empty<ProfileStrip>();

        /// <summary>Total road width in metres: sum of all strip widths.</summary>
        public float TotalWidth
        {
            get
            {
                return strips.Sum(strip => strip.width);
            }
        }

        /// <summary>Number of distinct material slots referenced by this profile.</summary>
        public int SubmeshCount
        {
            get
            {
                int max = strips.Select(strip => strip.materialIndex).Prepend(0).Max();
                return max + 1;
            }
        }
    }
}
