using System.Collections.Generic;
using CityBuilder.Infrastructure.Roads;
using Unity.Mathematics;
using UnityEngine;

#nullable enable
namespace CityBuilder.Rendering.Roads
{
    /// <summary>
    /// Builds a simplified closed collision mesh for roads.
    ///
    /// The visual road mesh is a flat, one-sided surface which PhysX cannot raycast
    /// reliably. This class produces a prism-shaped mesh – top face, bottom face,
    /// four longitudinal walls, and front/back caps – so that Physics.Raycast() hits
    /// the road from any angle.
    ///
    /// The mesh is intended solely for MeshCollider; it is never rendered.
    /// </summary>
    public static class RoadCollisionBuilder
    {
        private const int   MinSamples      = 4;
        private const int   MaxSamples      = 32;
        private const float MetersPerSample = 2f;

        /// <summary>
        /// Builds the collision mesh.
        ///
        /// Returns null for degenerate segments so callers can fall back to alternative
        /// collision strategies without allocating an empty mesh.
        ///
        /// thickness is the total height of the prism: the mesh extends ±(thickness/2)
        /// above and below the road surface centre. The default of 0.5 m is robust
        /// against minor terrain undulation.
        /// </summary>
        public static Mesh? Build(
            RoadSegment segment,
            RoadProfile profile,
            float3 nodeAPos,
            float3 nodeBPos,
            float thickness = 0.5f)
        {
            // TrimmedStartT/EndT are Bézier t-parameters, not normalised distances,
            // so we must convert them via the arc-length LUT.
            float arcStart = BezierCurve.TToArcLength(segment.ArcLengthLUT, segment.TrimmedStartT);
            float arcEnd   = BezierCurve.TToArcLength(segment.ArcLengthLUT, segment.TrimmedEndT);
            float arcSpan  = arcEnd - arcStart;

            if (arcSpan < 0.01f || profile.TotalWidth < 0.01f)
                return null;

            int   sampleCount   = Mathf.Clamp(Mathf.RoundToInt(arcSpan / MetersPerSample), MinSamples, MaxSamples);
            float halfWidth     = profile.TotalWidth * 0.5f;
            float halfThickness = thickness * 0.5f;

            // ── Sample the curve ──────────────────────────────────────────────
            // 4 vertices per sample: top-left (0), top-right (1), bottom-left (2), bottom-right (3)
            List<Vector3> verts = new(sampleCount * 4);

            for (int i = 0; i < sampleCount; i++)
            {
                float arcDist = arcStart + (float)i / (sampleCount - 1) * arcSpan;
                float t = BezierCurve.ArcLengthToT(segment.ArcLengthLUT, arcDist);
                float3 pos = BezierCurve.Evaluate(nodeAPos, segment.ControlPointA, segment.ControlPointB, nodeBPos, t);
                float3 tangent = BezierCurve.EvaluateTangent(nodeAPos, segment.ControlPointA, segment.ControlPointB, nodeBPos, t);
                Vector3 right = ComputeRightVector(tangent);
                Vector3 origin = new(pos.x, pos.y, pos.z);

                verts.Add(origin - right * halfWidth + Vector3.up * halfThickness);   // top-left
                verts.Add(origin + right * halfWidth + Vector3.up * halfThickness);   // top-right
                verts.Add(origin - right * halfWidth - Vector3.up * halfThickness);   // bottom-left
                verts.Add(origin + right * halfWidth - Vector3.up * halfThickness);   // bottom-right
            }

            // ── Build triangles ───────────────────────────────────────────────
            List<int> tris = new(sampleCount * 24 + 12);

            for (int i = 0; i < sampleCount - 1; i++)
            {
                int a = i * 4, b = (i + 1) * 4;

                // Winding is CCW when viewed from outside the prism (right-hand rule → outward normal).
                // Swapping the b/a+1 (or equivalent) pairs from the naive order was the root cause of
                // inside-out faces that PhysX backface-culling silently ignored.
                AddQuad(tris, a + 0, a + 1, b + 0, b + 1); // top face    – normal up   (+Y)
                AddQuad(tris, a + 2, b + 2, a + 3, b + 3); // bottom face – normal down (−Y)
                AddQuad(tris, a + 0, b + 0, a + 2, b + 2); // left wall   – normal outward left  (−right)
                AddQuad(tris, a + 1, a + 3, b + 1, b + 3); // right wall  – normal outward right (+right)
            }

            // Front cap – outward normal faces in −forward direction
            AddQuad(tris, 0, 2, 1, 3);

            // Back cap – outward normal faces in +forward direction
            int last = (sampleCount - 1) * 4;
            AddQuad(tris, last + 0, last + 1, last + 2, last + 3);

            // Physics-only mesh: normals are irrelevant to PhysX, skip RecalculateNormals.
            Mesh mesh = new() { name = "RoadCollisionMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddQuad(List<int> tris, int a, int b, int c, int d)
        {
            tris.Add(a); tris.Add(b); tris.Add(c);
            tris.Add(c); tris.Add(b); tris.Add(d);
        }

        private static Vector3 ComputeRightVector(float3 tangent)
        {
            float3 right = math.cross(tangent, new float3(0f, 1f, 0f));
            float  len   = math.length(right);
            return len > 0.0001f
                ? new Vector3(right.x / len, right.y / len, right.z / len)
                : Vector3.right;
        }
    }
}
