using System.Collections.Generic;
using CityBuilder.Infrastructure.Roads;
using Unity.Mathematics;
using UnityEngine;

#nullable enable
namespace CityBuilder.Rendering.Roads
{
    /// <summary>
    /// Builds the vertex data for a single road segment by extruding a cross-section
    /// profile along the segment's Bézier curve.
    ///
    /// This is a pure calculation class – no GameObjects, no MonoBehaviours, no Mesh
    /// objects. It reads RoadSegment curve data and writes plain arrays that the
    /// RoadRenderer can hand directly to Unity's Mesh API.
    ///
    /// How extrusion works:
    ///   1. Sample the Bézier curve at N arc-length-uniform positions within the
    ///      trimmed range [TrimmedStartT … TrimmedEndT].
    ///   2. At each sample, compute the road's forward direction (tangent) and its
    ///      right vector (perpendicular in the XZ plane).
    ///   3. Lay the profile strips across the road width by offsetting from the
    ///      curve centre along the right vector.
    ///   4. Connect adjacent cross-sections with quads → triangles.
    ///
    /// Each profile strip gets its own pair of vertices per sample so strips can use
    /// different materials without sharing boundary vertices.
    /// </summary>
    public static class RoadMeshBuilder
    {
        /// <summary>Number of cross-section samples per road segment.</summary>
        private const int SamplesPerSegment = 20;

        private static readonly Vector3 Up = Vector3.up;

        // ─────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the mesh data for a road segment.
        ///
        /// nodeAPos and nodeBPos are passed in explicitly because RoadSegment does not
        /// hold node positions itself – they live in RoadGraph to avoid duplication.
        ///
        /// Returns null when the profile has no strips or the segment has zero length.
        /// </summary>
        public static RoadMeshData? Build(
            RoadSegment segment,
            RoadProfile profile,
            float3      nodeAPos,
            float3      nodeBPos)
        {
            if (profile.Strips.Length == 0 || segment.TotalArcLength < 0.01f)
                return null;

            int sampleCount = SamplesPerSegment + 1;
            int stripCount = profile.Strips.Length;
            int submeshCount = profile.SubmeshCount;
            float totalWidth = profile.TotalWidth;

            // Precompute strip left-edge X offsets measured from the road centre
            float[] stripLeftX = ComputeStripLeftOffsets(profile, totalWidth);

            // ── Sample the curve ─────────────────────────────────────────────
            // Evenly spaced in real-world arc-length within the visible range
            float arcStart = segment.TrimmedStartT * segment.TotalArcLength;
            float arcEnd  = segment.TrimmedEndT * segment.TotalArcLength;
            float arcSpan = arcEnd - arcStart;

            if (arcSpan < 0.01f)
                return null;

            float3[] positions = new float3[sampleCount];
            float3[] tangents  = new float3[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float arcDist = arcStart + (float)i / SamplesPerSegment * arcSpan;
                float t       = BezierCurve.ArcLengthToT(segment.ArcLengthLUT, arcDist);
                positions[i]  = BezierCurve.Evaluate(nodeAPos, segment.ControlPointA, segment.ControlPointB, nodeBPos, t);
                tangents[i]   = BezierCurve.EvaluateTangent(nodeAPos, segment.ControlPointA, segment.ControlPointB, nodeBPos, t);
            }

            // ── Allocate output arrays ────────────────────────────────────────
            // Each strip owns 2 vertices per sample (left edge + right edge)
            // so adjacent strips with different materials have independent vertices.
            int vertexCount = sampleCount * stripCount * 2;
            Vector3[] vertices  = new Vector3[vertexCount];
            Vector3[] normals   = new Vector3[vertexCount];
            Vector2[] uvs       = new Vector2[vertexCount];

            // Collect triangle lists per submesh, then convert to arrays at the end
            List<int>[] triLists = new List<int>[submeshCount];
            for (int s = 0; s < submeshCount; s++)
                triLists[s] = new List<int>(SamplesPerSegment * 6);

            // ── Fill vertices ─────────────────────────────────────────────────
            for (int sampleIdx = 0; sampleIdx < sampleCount; sampleIdx++)
            {
                float3  tangent = tangents[sampleIdx];
                Vector3 right   = ComputeRightVector(tangent);
                float   vCoord  = (float)sampleIdx / SamplesPerSegment;

                for (int stripIdx = 0; stripIdx < stripCount; stripIdx++)
                {
                    ProfileStrip strip     = profile.Strips[stripIdx];
                    float        leftX     = stripLeftX[stripIdx];
                    float        rightX    = leftX + strip.Width;
                    float        height    = strip.HeightOffset;
                    Vector3      origin    = new (positions[sampleIdx].x, positions[sampleIdx].y, positions[sampleIdx].z);

                    int leftIdx  = SampleStripVertex(sampleIdx, stripIdx, 0, stripCount);
                    int rightIdx = SampleStripVertex(sampleIdx, stripIdx, 1, stripCount);

                    vertices[leftIdx]  = origin + right * leftX  + Up * height;
                    vertices[rightIdx] = origin + right * rightX + Up * height;

                    // UV: U spans the strip width (0 = left, 1 = right); V tiles along road
                    uvs[leftIdx]  = new Vector2(0f, vCoord);
                    uvs[rightIdx] = new Vector2(1f, vCoord);

                    normals[leftIdx]  = Up;
                    normals[rightIdx] = Up;
                }
            }

            // ── Build triangles ───────────────────────────────────────────────
            for (int sampleIdx = 0; sampleIdx < SamplesPerSegment; sampleIdx++)
            {
                for (int stripIdx = 0; stripIdx < stripCount; stripIdx++)
                {
                    ProfileStrip strip = profile.Strips[stripIdx];

                    // Four corners of the road quad between sample sampleIdx and sampleIdx+1
                    int topLeft     = SampleStripVertex(sampleIdx,     stripIdx, 0, stripCount);
                    int topRight    = SampleStripVertex(sampleIdx,     stripIdx, 1, stripCount);
                    int bottomLeft  = SampleStripVertex(sampleIdx + 1, stripIdx, 0, stripCount);
                    int bottomRight = SampleStripVertex(sampleIdx + 1, stripIdx, 1, stripCount);

                    // Two CCW triangles (normal faces upward)
                    List<int> tris = triLists[strip.MaterialIndex];
                    tris.Add(topLeft);
                    tris.Add(bottomLeft);
                    tris.Add(topRight);

                    tris.Add(topRight);
                    tris.Add(bottomLeft);
                    tris.Add(bottomRight);
                }
            }

            // ── Convert triangle lists to arrays ──────────────────────────────
            int[][] triangles = new int[submeshCount][];
            for (int s = 0; s < submeshCount; s++)
                triangles[s] = triLists[s].ToArray();

            return new RoadMeshData(vertices, normals, uvs, triangles);
        }

        // ─────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Flat vertex index for (sampleIdx, stripIdx, side) where side 0 = left, 1 = right.
        /// Each strip owns 2 vertices per sample, all strips are packed in the same array.
        /// </summary>
        private static int SampleStripVertex(int sampleIdx, int stripIdx, int side, int stripCount) =>
            sampleIdx * stripCount * 2 + stripIdx * 2 + side;

        /// <summary>
        /// Left-edge X offset of each strip measured from the road centre line.
        /// Strips are laid left to right; the first strip starts at -totalWidth/2.
        /// </summary>
        private static float[] ComputeStripLeftOffsets(RoadProfile profile, float totalWidth)
        {
            float[] offsets = new float[profile.Strips.Length];
            float   cursor  = -totalWidth * 0.5f;

            for (int i = 0; i < profile.Strips.Length; i++)
            {
                offsets[i] = cursor;
                cursor    += profile.Strips[i].Width;
            }

            return offsets;
        }

        /// <summary>
        /// Computes the road's right vector from the forward tangent.
        /// Roads are assumed to lie in the XZ plane; the right vector is the tangent
        /// rotated 90° clockwise around the Y axis.
        /// Falls back to (1, 0, 0) for degenerate tangents (zero-length curves).
        /// </summary>
        private static Vector3 ComputeRightVector(float3 tangent)
        {
            // cross(tangent, up) = (tangent.z, 0, -tangent.x) – points right of travel
            float3 right = math.cross(tangent, new float3(0f, 1f, 0f));
            float  len   = math.length(right);
            return len > 0.0001f
                ? new Vector3(right.x / len, right.y / len, right.z / len)
                : Vector3.right;
        }
    }
}
